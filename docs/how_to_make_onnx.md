# シアトル用ONNXモデル作成手順

### 1. 事前準備
- [how_to_get_csv.md](how_to_get_csv.md)の手順で入手したCSVファイルを、プロジェクトのルートディレクトリ（各Pythonスクリプトがある場所）に配置します。

- ターミナルを開き、プロジェクトのルートディレクトリ（各Pythonスクリプトがある場所）で以下の順にコマンドを実行してください。

### 2. 依存ライブラリのインストール
まず、環境を最新の状態に整えます。
```Bash
pip install -r requirements.txt
```

### 3. データの前処理（シアトル抽出 ＆ マージ）

- `01_prepare_data.py` を使い、シアトルのステーションID（`USW00024233`）を指定して実行します。これにより、外気温を考慮した学習用データが生成されます。

```Bash
python 01_prepare_data.py --station USW00024233
```
- 生成物: `data/processed/` 内に `X.npy, y.npy, scaler_params.json`

### 4. モデルの学習
（PyTorch）LSTMモデルを訓練します。前処理で作成されたシアトル専用のデータが読み込まれます。
```Bash
python 02_train_model.py
```

- 生成物: `models/repair_model_best.pt`

### 5. ONNXへの変換と検証
最後に、学習済みモデルをONNX形式にエクスポートし、推論誤差がないか検証します。
```Bash
python 03_export_onnx.py --verify
```

- 生成物: `models/repair_model.onnx, models/model_manifest.json`

#### 実行時のWarning
- `opses_version 17` の引き上げ: PyTorch 2.x系が「より新しい機能を使いたいから自動でversion 18として書き出したよ」と言っているだけです。C#のONNX Runtime（1.16以上）はversion 18を問題なく解釈できるので大丈夫です。

- `torchvision is not installed`: 画像処理ライブラリがないという通知ですが、今回は時系列データ（数値）のみを扱っているため不要です。

- `dynamic_axes` の推奨事項: 新しいエクスポートエンジン（Dynamo）での書き方の作法が変わったことによる警告ですが、ファイル自体は正しく生成されています。

- `FutureWarning`: Python 3.13などの最新環境を使っているために、ライブラリ内部の古い書き方に注意が出ているだけです。

## 生成されたファイルの役割（C#実装に向けて）

- モデルが完成したら、以下の3つのファイルをC#プロジェクト（AWS LambdaやAzure Functionsなど）へ持っていく準備をしてください。

|ファイル名|役割|
| -- | -- |
|`repair_model.onnx`|推論エンジン本体。C#の Microsoft.ML.OnnxRuntime で読み込みます。|
|`scaler_params.json`|正規化パラメータ。C#側に入力する値を 0~1 に変換し、予測結果を℃に戻すために必須です。|
|`model_manifest.json`|真正性証明書。SHA-256ハッシュ値が含まれており、ブロックチェーンへのアンカーリングに使用します。|

## 横浜用データを作成する場合

シアトル用のモデルが完成した後、もし続けて横浜用（輸入側）も作成する場合は、`models/` フォルダの中身を一度別の場所（`models_seattle/` など）にコピーしてから、手順2の `--station` 引数を `JA000047670` に変えて再実行してください。

## 次のフェーズ：C# への組み込み
次はこれを C# (.NET) のプロジェクトで動かすフェーズです。

実装すべきは、以下の機能を持つ `InferenceEngine` クラスです。

1. モデルのロード: `Microsoft.ML.OnnxRuntime` を使用。

1. 正規化の適用: `scaler_params.json` の値を使って、入力データを 0~1 に変換。

1. 推論の実行: 直近10個のデータ（`Window Size`）を入力。

1. 逆正規化: 推論結果を元の「温度（℃）」に戻す。
