"""
02_train_model.py
=================
PyTorch LSTM を使ったセンサー異常修復モデルの学習。

入力:
  - data/processed/X.npy         : 学習用特徴量 (N, window_size)
  - data/processed/y.npy         : 正解ラベル   (N,)
  - data/processed/scaler_params.json

出力:
  - models/repair_model.pt        : PyTorch チェックポイント
  - models/training_log.json      : 損失推移ログ

実行方法:
  pip install torch numpy scikit-learn matplotlib
  python 02_train_model.py

  # GPU がある場合は自動的に CUDA を使用します
  # エポック数・学習率を変更する場合:
  python 02_train_model.py --epochs 100 --lr 0.001
"""

import argparse
import json
import os
import time
import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import DataLoader, TensorDataset, random_split
import matplotlib
matplotlib.use("Agg")   # GUI なし環境用
import matplotlib.pyplot as plt

# ── 定数 ──────────────────────────────────────────────────────────────────

WINDOW_SIZE  = 10
HIDDEN_SIZE  = 64
NUM_LAYERS   = 2
DROPOUT      = 0.2
BATCH_SIZE   = 256
DEFAULT_EPOCHS = 50
DEFAULT_LR   = 5e-4
TRAIN_RATIO  = 0.8
VAL_RATIO    = 0.1
# TEST_RATIO = 0.1 (残り)
RANDOM_SEED  = 42
MODEL_DIR    = "models"
DATA_DIR     = "data/processed"

torch.manual_seed(RANDOM_SEED)
np.random.seed(RANDOM_SEED)

# ── モデル定義 ─────────────────────────────────────────────────────────────

class SensorRepairLSTM(nn.Module):
    """
    センサー時系列修復 LSTM モデル。

    アーキテクチャ:
      Input  (batch, seq_len=WINDOW_SIZE, input_size=1)
        └─ LSTM × NUM_LAYERS (hidden=HIDDEN_SIZE, dropout=DROPOUT)
        └─ 最終ステップの隠れ状態 (batch, HIDDEN_SIZE)
        └─ FC (HIDDEN_SIZE → 32 → 1)
      Output (batch, 1)  — 修復後の正規化済み値

    ONNX エクスポート時の仕様:
      input  名: "input"   shape: [batch, WINDOW_SIZE, 1]   dtype: float32
      output 名: "output"  shape: [batch, 1]                dtype: float32
    """
    def __init__(self,
                 input_size:  int = 1,
                 hidden_size: int = HIDDEN_SIZE,
                 num_layers:  int = NUM_LAYERS,
                 dropout:     float = DROPOUT):
        super().__init__()
        self.hidden_size = hidden_size
        self.num_layers  = num_layers

        self.lstm = nn.LSTM(
            input_size=input_size,
            hidden_size=hidden_size,
            num_layers=num_layers,
            batch_first=True,
            dropout=dropout if num_layers > 1 else 0.0,
        )
        self.head = nn.Sequential(
            nn.Linear(hidden_size, 32),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(32, 1),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x: (batch, seq_len, 1)
        out, _ = self.lstm(x)         # out: (batch, seq_len, hidden)
        last   = out[:, -1, :]        # 最終ステップのみ使用
        return self.head(last)         # (batch, 1)


# ── データ読み込み ────────────────────────────────────────────────────────

def load_data(data_dir: str = DATA_DIR):
    X = np.load(os.path.join(data_dir, "X.npy")).astype(np.float32)
    y = np.load(os.path.join(data_dir, "y.npy")).astype(np.float32)
    print(f"[Data] X: {X.shape}, y: {y.shape}")

    # LSTM 入力形式: (N, seq_len, features)
    X_tensor = torch.from_numpy(X).unsqueeze(-1)   # (N, WINDOW_SIZE, 1)
    y_tensor = torch.from_numpy(y).unsqueeze(-1)   # (N, 1)

    dataset = TensorDataset(X_tensor, y_tensor)
    n = len(dataset)
    n_train = int(n * TRAIN_RATIO)
    n_val   = int(n * VAL_RATIO)
    n_test  = n - n_train - n_val

    train_ds, val_ds, test_ds = random_split(
        dataset, [n_train, n_val, n_test],
        generator=torch.Generator().manual_seed(RANDOM_SEED)
    )
    print(f"[Split] train={n_train:,} val={n_val:,} test={n_test:,}")
    return train_ds, val_ds, test_ds


# ── 学習ループ ────────────────────────────────────────────────────────────

def train(model: nn.Module,
          train_loader: DataLoader,
          val_loader: DataLoader,
          epochs: int,
          lr: float,
          device: torch.device) -> dict:

    optimizer = torch.optim.Adam(model.parameters(), lr=lr, weight_decay=1e-5)
    scheduler = torch.optim.lr_scheduler.ReduceLROnPlateau(
        optimizer, mode="min", factor=0.5, patience=5
    )
    criterion = nn.MSELoss()

    best_val_loss = float("inf")
    best_epoch    = 0
    log = {"train_loss": [], "val_loss": [], "lr": []}

    print(f"\n[Train] device={device}, epochs={epochs}, lr={lr}")
    print("-" * 60)

    for epoch in range(1, epochs + 1):
        # ── 訓練フェーズ ─────────────────────────────────────────────────
        model.train()
        train_loss = 0.0
        t0 = time.time()

        for X_batch, y_batch in train_loader:
            X_batch, y_batch = X_batch.to(device), y_batch.to(device)
            optimizer.zero_grad()
            pred = model(X_batch)
            loss = criterion(pred, y_batch)
            loss.backward()
            # 勾配クリッピング (LSTM の勾配爆発防止)
            nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
            optimizer.step()
            train_loss += loss.item() * len(X_batch)

        train_loss /= len(train_loader.dataset)

        # ── 検証フェーズ ─────────────────────────────────────────────────
        model.eval()
        val_loss = 0.0
        with torch.no_grad():
            for X_batch, y_batch in val_loader:
                X_batch, y_batch = X_batch.to(device), y_batch.to(device)
                pred = model(X_batch)
                val_loss += criterion(pred, y_batch).item() * len(X_batch)
        val_loss /= len(val_loader.dataset)

        scheduler.step(val_loss)
        current_lr = optimizer.param_groups[0]["lr"]
        elapsed = time.time() - t0

        log["train_loss"].append(round(train_loss, 6))
        log["val_loss"].append(round(val_loss, 6))
        log["lr"].append(current_lr)

        if epoch % 10 == 0 or epoch == 1:
            print(f"Epoch {epoch:3d}/{epochs} "
                  f"| train={train_loss:.6f} val={val_loss:.6f} "
                  f"| lr={current_lr:.2e} | {elapsed:.1f}s")

        # ── ベストモデル保存 ─────────────────────────────────────────────
        if val_loss < best_val_loss:
            best_val_loss = val_loss
            best_epoch    = epoch
            torch.save({
                "epoch":      epoch,
                "model_state": model.state_dict(),
                "val_loss":   val_loss,
                "config": {
                    "window_size": WINDOW_SIZE,
                    "hidden_size": HIDDEN_SIZE,
                    "num_layers":  NUM_LAYERS,
                    "dropout":     DROPOUT,
                }
            }, os.path.join(MODEL_DIR, "repair_model_best.pt"))

    print("-" * 60)
    print(f"[Train] 完了: best_epoch={best_epoch}, best_val_loss={best_val_loss:.6f}")
    return log


# ── テスト評価 ────────────────────────────────────────────────────────────

def evaluate(model: nn.Module,
             test_loader: DataLoader,
             device: torch.device) -> dict:
    criterion = nn.MSELoss()
    model.eval()
    total_loss = 0.0
    preds, targets = [], []

    with torch.no_grad():
        for X_batch, y_batch in test_loader:
            X_batch, y_batch = X_batch.to(device), y_batch.to(device)
            pred = model(X_batch)
            total_loss += criterion(pred, y_batch).item() * len(X_batch)
            preds.extend(pred.cpu().numpy().flatten().tolist())
            targets.extend(y_batch.cpu().numpy().flatten().tolist())

    mse  = total_loss / len(test_loader.dataset)
    rmse = mse ** 0.5
    mae  = float(np.mean(np.abs(np.array(preds) - np.array(targets))))

    metrics = {"test_mse": round(mse, 6),
               "test_rmse": round(rmse, 6),
               "test_mae": round(mae, 6)}
    print(f"[Evaluate] MSE={mse:.6f} RMSE={rmse:.6f} MAE={mae:.6f}")
    return metrics


# ── 損失グラフ保存 ────────────────────────────────────────────────────────

def plot_loss(log: dict, save_path: str):
    fig, ax = plt.subplots(figsize=(8, 4))
    ax.plot(log["train_loss"], label="Train Loss")
    ax.plot(log["val_loss"],   label="Val Loss")
    ax.set_xlabel("Epoch")
    ax.set_ylabel("MSE Loss")
    ax.set_title("Training / Validation Loss")
    ax.legend()
    ax.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(save_path, dpi=120)
    plt.close()
    print(f"[Plot] 損失グラフ保存: {save_path}")


# ── メイン ────────────────────────────────────────────────────────────────

def main(epochs: int = DEFAULT_EPOCHS, lr: float = DEFAULT_LR):
    os.makedirs(MODEL_DIR, exist_ok=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"[Device] {device}")

    # ── データ準備 ────────────────────────────────────────────────────────
    train_ds, val_ds, test_ds = load_data()
    train_loader = DataLoader(train_ds, batch_size=BATCH_SIZE, shuffle=True,  num_workers=0)
    val_loader   = DataLoader(val_ds,   batch_size=BATCH_SIZE, shuffle=False, num_workers=0)
    test_loader  = DataLoader(test_ds,  batch_size=BATCH_SIZE, shuffle=False, num_workers=0)

    # ── モデル初期化 ─────────────────────────────────────────────────────
    model = SensorRepairLSTM().to(device)
    total_params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    print(f"[Model] パラメータ数: {total_params:,}")

    # ── 学習 ──────────────────────────────────────────────────────────────
    log = train(model, train_loader, val_loader, epochs, lr, device)

    # ── ベストモデルをロードしてテスト評価 ──────────────────────────────
    ckpt = torch.load(os.path.join(MODEL_DIR, "repair_model_best.pt"),
                      map_location=device)
    model.load_state_dict(ckpt["model_state"])
    metrics = evaluate(model, test_loader, device)

    # ── ログ保存 ──────────────────────────────────────────────────────────
    training_log = {
        "epochs":         epochs,
        "lr":             lr,
        "best_epoch":     ckpt["epoch"],
        "best_val_loss":  ckpt["val_loss"],
        "metrics":        metrics,
        "loss_history":   log,
        "model_config":   ckpt["config"],
        "device":         str(device),
        "trained_at":     time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    log_path = os.path.join(MODEL_DIR, "training_log.json")
    with open(log_path, "w") as f:
        json.dump(training_log, f, indent=2)
    print(f"[Log] {log_path} に保存")

    plot_loss(log, os.path.join(MODEL_DIR, "loss_curve.png"))

    print("\n=== 学習完了 ===")
    print(f"  次のステップ: python 03_export_onnx.py")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int, default=DEFAULT_EPOCHS)
    parser.add_argument("--lr",     type=float, default=DEFAULT_LR)
    args = parser.parse_args()
    main(epochs=args.epochs, lr=args.lr)
