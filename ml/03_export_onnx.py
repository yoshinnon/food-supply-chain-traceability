"""
03_export_onnx.py
=================
学習済み PyTorch モデルを ONNX 形式に変換し、
SHA-256 ハッシュを出力して C# 側の OnnxInferenceEngine に接続する。

入力:
  - models/repair_model_best.pt   : 02_train_model.py で生成したチェックポイント

出力:
  - models/repair_model.onnx      : ONNX モデルファイル
  - models/model_manifest.json    : ハッシュ・メタデータ (両クラウドに配布)

実行方法:
  pip install torch onnx onnxruntime numpy
  python 03_export_onnx.py

  # 精度検証もする場合:
  python 03_export_onnx.py --verify
"""

import argparse
import hashlib
import json
import os
import time
import numpy as np
import torch
import torch.nn as nn
import onnx
import onnxruntime as ort

# 02_train_model.py と共通のモデル定義を再利用
WINDOW_SIZE  = 10
HIDDEN_SIZE  = 64
NUM_LAYERS   = 2
DROPOUT      = 0.2
MODEL_DIR    = "models"
DATA_DIR     = "data/processed"


# ── モデル定義 (02_train_model.py と同一) ─────────────────────────────────

class SensorRepairLSTM(nn.Module):
    def __init__(self, input_size=1, hidden_size=HIDDEN_SIZE,
                 num_layers=NUM_LAYERS, dropout=DROPOUT):
        super().__init__()
        self.lstm = nn.LSTM(input_size=input_size, hidden_size=hidden_size,
                            num_layers=num_layers, batch_first=True,
                            dropout=dropout if num_layers > 1 else 0.0)
        self.head = nn.Sequential(
            nn.Linear(hidden_size, 32), nn.ReLU(),
            nn.Dropout(dropout), nn.Linear(32, 1),
        )
    def forward(self, x):
        out, _ = self.lstm(x)
        return self.head(out[:, -1, :])


# ── ハッシュ計算 ─────────────────────────────────────────────────────────

def compute_sha256(path: str) -> str:
    """ファイルの SHA-256 を計算する。OnnxInferenceEngine と同じロジック。"""
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


# ── ONNX エクスポート ──────────────────────────────────────────────────────

def export_onnx(model: nn.Module, output_path: str) -> str:
    """
    PyTorch モデルを ONNX 形式でエクスポートする。

    入出力ノード名は OnnxInferenceEngine.cs の設定と一致させる:
      InputNodeName  = "input"
      OutputNodeName = "output"
    """
    model.eval()

    # ダミー入力 (batch=1, seq_len=WINDOW_SIZE, features=1)
    dummy_input = torch.zeros(1, WINDOW_SIZE, 1, dtype=torch.float32)

    torch.onnx.export(
        model,
        dummy_input,
        output_path,
        export_params=True,
        opset_version=17,           # ORT 1.16+ でサポートする最新安定版
        do_constant_folding=True,   # 定数畳み込みで推論速度を向上
        input_names=["input"],      # OnnxInferenceEngine.InputNodeName と一致
        output_names=["output"],    # OnnxInferenceEngine.OutputNodeName と一致
        dynamic_axes={
            "input":  {0: "batch_size"},  # バッチサイズを動的に
            "output": {0: "batch_size"},
        },
    )
    print(f"[Export] ONNX エクスポート完了: {output_path}")
    return output_path


# ── ONNX モデル検証 ──────────────────────────────────────────────────────

def verify_onnx(onnx_path: str, pt_model: nn.Module,
                n_samples: int = 100) -> dict:
    """
    PyTorch モデルと ONNX Runtime の出力を比較して精度を検証する。
    最大絶対誤差が 1e-4 以内であれば変換成功とみなす。
    """
    # ONNX モデルの構文チェック
    onnx_model = onnx.load(onnx_path)
    onnx.checker.check_model(onnx_model)
    print("[Verify] ONNX 構文チェック OK")

    # ORT セッション
    sess = ort.InferenceSession(onnx_path,
                                providers=["CPUExecutionProvider"])
    input_name  = sess.get_inputs()[0].name
    output_name = sess.get_outputs()[0].name
    print(f"[Verify] input={input_name}, output={output_name}")

    # ランダムサンプルで PyTorch vs ORT を比較
    np.random.seed(42)
    test_inputs = np.random.randn(n_samples, WINDOW_SIZE, 1).astype(np.float32)
    errors = []

    pt_model.eval()
    with torch.no_grad():
        for i in range(n_samples):
            x_np = test_inputs[i:i+1]          # (1, WINDOW_SIZE, 1)
            x_pt = torch.from_numpy(x_np)

            pt_out  = pt_model(x_pt).numpy()[0, 0]
            ort_out = sess.run([output_name], {input_name: x_np})[0][0, 0]
            errors.append(abs(float(pt_out) - float(ort_out)))

    max_err  = max(errors)
    mean_err = sum(errors) / len(errors)
    passed   = max_err < 1e-4

    print(f"[Verify] max_err={max_err:.2e}  mean_err={mean_err:.2e}  "
          f"{'✅ PASS' if passed else '❌ FAIL'}")

    if not passed:
        raise RuntimeError(
            f"ONNX 変換精度検証失敗: max_err={max_err:.2e} (閾値: 1e-4)\n"
            "opset_version を下げるか、モデル構造を確認してください。"
        )

    return {"max_abs_error": max_err, "mean_abs_error": mean_err,
            "n_samples": n_samples, "passed": passed}


# ── モデルサイズ確認 ─────────────────────────────────────────────────────

def get_model_info(onnx_path: str) -> dict:
    model = onnx.load(onnx_path)
    size_kb = os.path.getsize(onnx_path) / 1024
    n_nodes = len(model.graph.node)
    inputs  = [f"{i.name}: {list(i.type.tensor_type.shape.dim)}"
               for i in model.graph.input]
    outputs = [f"{o.name}: {list(o.type.tensor_type.shape.dim)}"
               for o in model.graph.output]
    return {"size_kb": round(size_kb, 1),
            "graph_nodes": n_nodes,
            "inputs": inputs, "outputs": outputs}


# ── マニフェスト生成 ─────────────────────────────────────────────────────

def generate_manifest(onnx_path: str,
                      model_hash: str,
                      verify_result: dict | None,
                      training_log: dict | None,
                      model_info: dict) -> dict:
    """
    両クラウドに配布するモデルマニフェストを生成する。

    このファイルの model_hash を
      - AWS Lambda 環境変数  ONNX_MODEL_HASH
      - Azure Functions 環境変数 ONNX_MODEL_HASH
    に設定することで、OnnxInferenceEngine が起動時に同一性を検証する。
    """
    manifest = {
        "model_name":     "sensor-repair-lstm",
        "version":        time.strftime("%Y%m%d-%H%M%S", time.gmtime()),
        "model_hash":     model_hash,       # ← これを ONNX_MODEL_HASH に設定
        "onnx_opset":     17,
        "window_size":    WINDOW_SIZE,      # AiRepairOptions.WindowSize と揃える
        "input_node":     "input",          # OnnxEngineOptions.InputNodeName
        "output_node":    "output",         # OnnxEngineOptions.OutputNodeName
        "model_info":     model_info,
        "verify":         verify_result,
        "training":       {
            "best_val_loss": training_log.get("best_val_loss") if training_log else None,
            "metrics":       training_log.get("metrics")       if training_log else None,
            "trained_at":    training_log.get("trained_at")    if training_log else None,
        },
        "exported_at":    time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "deploy": {
            "aws": {
                "s3_path":          "s3://traceability-models/prod/repair_model.onnx",
                "lambda_layer_path": "/opt/models/repair_model.onnx",
                "env_var":          f"ONNX_MODEL_HASH={model_hash}",
            },
            "azure": {
                "blob_path":        "https://<storage>.blob.core.windows.net/models/repair_model.onnx",
                "function_path":    "/home/site/wwwroot/models/repair_model.onnx",
                "env_var":          f"ONNX_MODEL_HASH={model_hash}",
            },
        },
    }
    return manifest


# ── デプロイ手順表示 ─────────────────────────────────────────────────────

def print_deploy_instructions(manifest: dict):
    h = manifest["model_hash"]
    print("\n" + "=" * 65)
    print("  デプロイ手順")
    print("=" * 65)
    print(f"\n【ステップ 1】 モデルハッシュ確認")
    print(f"  ONNX_MODEL_HASH={h}")
    print(f"\n【ステップ 2】 AWS S3 へアップロード")
    print(f"  aws s3 cp models/repair_model.onnx \\")
    print(f"    s3://traceability-models/prod/repair_model.onnx")
    print(f"\n【ステップ 3】 Lambda 環境変数を設定")
    print(f"  aws lambda update-function-configuration \\")
    print(f"    --function-name traceability-processor-prod \\")
    print(f"    --environment Variables={{ONNX_MODEL_HASH={h}}}")
    print(f"\n【ステップ 4】 Azure Blob Storage へアップロード")
    print(f"  az storage blob upload \\")
    print(f"    --file models/repair_model.onnx \\")
    print(f"    --container-name models \\")
    print(f"    --name prod/repair_model.onnx")
    print(f"\n【ステップ 5】 Azure Functions 環境変数を設定")
    print(f"  az functionapp config appsettings set \\")
    print(f"    --name func-traceability-prod \\")
    print(f"    --resource-group rg-traceability-prod \\")
    print(f"    --settings ONNX_MODEL_HASH={h}")
    print(f"\n【確認】 両クラウドで同一ハッシュが設定されたか検証")
    print(f"  aws lambda get-function-configuration \\")
    print(f"    --function-name traceability-processor-prod \\")
    print(f"    --query 'Environment.Variables.ONNX_MODEL_HASH'")
    print(f"  az functionapp config appsettings list \\")
    print(f"    --name func-traceability-prod \\")
    print(f"    --resource-group rg-traceability-prod \\")
    print(f"    --query \"[?name=='ONNX_MODEL_HASH'].value\"")
    print("=" * 65)


# ── メイン ────────────────────────────────────────────────────────────────

def main(verify: bool = False):
    os.makedirs(MODEL_DIR, exist_ok=True)

    pt_path   = os.path.join(MODEL_DIR, "repair_model_best.pt")
    onnx_path = os.path.join(MODEL_DIR, "repair_model.onnx")

    if not os.path.exists(pt_path):
        raise FileNotFoundError(
            f"{pt_path} が見つかりません。\n"
            "先に python 02_train_model.py を実行してください。"
        )

    # ── Step 1: モデル読み込み ────────────────────────────────────────────
    print("\n=== Step 1: PyTorch モデル読み込み ===")
    ckpt  = torch.load(pt_path, map_location="cpu")
    model = SensorRepairLSTM()
    model.load_state_dict(ckpt["model_state"])
    model.eval()
    print(f"  best_epoch={ckpt['epoch']}, val_loss={ckpt['val_loss']:.6f}")

    # ── Step 2: ONNX エクスポート ────────────────────────────────────────
    print("\n=== Step 2: ONNX エクスポート ===")
    export_onnx(model, onnx_path)

    # ── Step 3: SHA-256 ハッシュ計算 ─────────────────────────────────────
    print("\n=== Step 3: SHA-256 ハッシュ計算 ===")
    model_hash = compute_sha256(onnx_path)
    print(f"  ONNX_MODEL_HASH={model_hash}")

    # ── Step 4: 精度検証 (オプション) ───────────────────────────────────
    verify_result = None
    if verify:
        print("\n=== Step 4: 精度検証 (PyTorch vs ORT) ===")
        verify_result = verify_onnx(onnx_path, model)

    # ── Step 5: モデル情報・マニフェスト生成 ────────────────────────────
    print("\n=== Step 5: マニフェスト生成 ===")
    model_info = get_model_info(onnx_path)
    print(f"  サイズ: {model_info['size_kb']} KB, ノード数: {model_info['graph_nodes']}")

    training_log = None
    log_path = os.path.join(MODEL_DIR, "training_log.json")
    if os.path.exists(log_path):
        with open(log_path) as f:
            training_log = json.load(f)

    manifest = generate_manifest(
        onnx_path, model_hash, verify_result, training_log, model_info
    )

    manifest_path = os.path.join(MODEL_DIR, "model_manifest.json")
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    print(f"  → {manifest_path} に保存")

    # ── Step 6: デプロイ手順表示 ────────────────────────────────────────
    print_deploy_instructions(manifest)

    print("\n=== ONNX 変換完了 ===")
    print(f"  出力ファイル : {onnx_path}")
    print(f"  マニフェスト : {manifest_path}")
    print(f"  ONNX_MODEL_HASH={model_hash}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--verify", action="store_true",
                        help="PyTorch と ORT の出力を比較して精度検証する")
    args = parser.parse_args()
    main(verify=args.verify)
