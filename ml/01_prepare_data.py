"""
01_prepare_data.py (修正・拡張版)
================================
各入力データファイルを引数で指定し、ONNX モデル学習用データを生成します。

実行例:
  # シアトル用のデータを準備（デフォルトファイル名を使用）
  python 01_prepare_data.py --station USW00024233
  
  # ファイル名を明示的に指定して横浜用のデータを準備
  python 01_prepare_data.py \
    --logistics smart_logistics_dataset.csv \
    --noaa 4286573.csv \
    --station JA000047670

  デフォルト値:
    空港の場所(station): USW00024233 // シアトル
    Kaggle物流CSVのパス(logistics): smart_logistics_dataset.csv
    NOAA気象CSVのパス(noaa): 4286573.csv // シアトルと横浜のデータが結合されたCSVを想定
"""

import argparse
import json
import os
import numpy as np
import pandas as pd
from sklearn.preprocessing import MinMaxScaler
import warnings
warnings.filterwarnings("ignore")

# ── 定数 ──────────────────────────────────────────────────────────────────

WINDOW_SIZE = 10          # OnnxInferenceEngine の WindowSize と合わせる
ANOMALY_RATE = 0.05       # 全データの 5% に異常を注入
RANDOM_SEED = 42
OUTPUT_DIR = "data/processed"

# ── データ処理関数 ──────────────────────────────────────────────────────

def prepare_merged_data(logistics_path, noaa_path, station_id):
    """KaggleログとNOAAデータを読み込み、地点を絞り込んでマージする"""
    print(f"[*] 入力ファイルを読み込み中...")
    print(f"    - 物流データ: {logistics_path}")
    print(f"    - 気象データ: {noaa_path}")
    
    if not os.path.exists(logistics_path):
        raise FileNotFoundError(f"物流データが見つかりません: {logistics_path}")
    if not os.path.exists(noaa_path):
        raise FileNotFoundError(f"気象データが見つかりません: {noaa_path}")

    # 1. 読み込み
    df_log = pd.read_csv(logistics_path)
    df_noaa = pd.read_csv(noaa_path)

    # 2. 型変換とソート
    # Kaggle側は 'Timestamp'、NOAA側は 'DATE' カラムを使用
    df_log['Timestamp'] = pd.to_datetime(df_log['Timestamp'])
    df_noaa['DATE'] = pd.to_datetime(df_noaa['DATE'])
    df_log = df_log.sort_values('Timestamp')

    # 3. 指定地点の抽出
    print(f"[*] ステーションID '{station_id}' のデータを抽出中...")
    df_station = df_noaa[df_noaa['STATION'] == station_id].copy()
    if df_station.empty:
        available_stations = df_noaa['STATION'].unique()
        raise ValueError(f"Station ID '{station_id}' が見つかりません。利用可能: {available_stations}")
    
    df_station = df_station.sort_values('DATE')
    
    # 4. 近似マージ (merge_asof)
    print("[*] タイムスタンプによる近似マージを実行中...")
    merged = pd.merge_asof(
        df_log, 
        df_station[['DATE', 'TAVG', 'TMAX', 'TMIN']], 
        left_on='Timestamp', 
        right_on='DATE', 
        direction='backward'
    )
    
    # 5. 気温データの補完
    merged['TAVG'] = merged['TAVG'].fillna((merged['TMAX'] + merged['TMIN']) / 2)
    merged['TAVG'] = merged['TAVG'].fillna(merged['TAVG'].mean())
    
    return merged[['Timestamp', 'Temperature', 'TAVG']]

def inject_anomalies(df):
    """学習用に人工的な異常値を注入する"""
    print(f"[*] 異常値を注入中... (Rate: {ANOMALY_RATE})")
    np.random.seed(RANDOM_SEED)
    df = df.copy()
    df['is_anomaly'] = 0
    
    n_anomalies = int(len(df) * ANOMALY_RATE)
    indices = np.random.choice(df.index, n_anomalies, replace=False)
    
    # 異常パターン: 外気温に対して ±15〜25度の乖離を発生させる
    df.loc[indices, 'Temperature'] += np.random.uniform(15, 25, size=n_anomalies) * np.random.choice([-1, 1], size=n_anomalies)
    df.loc[indices, 'is_anomaly'] = 1
    
    return df

def normalize_features(df):
    """MinMaxScalerを使用して正規化し、パラメータを保存する"""
    scaler = MinMaxScaler()
    features = ['Temperature', 'TAVG']
    df[features] = scaler.fit_transform(df[features])
    
    params = {
        "min": scaler.data_min_.tolist(),
        "max": scaler.data_max_.tolist(),
        "feature_names": features
    }
    
    # 異常検知の統計
    anomaly_stats = {
        "total_count": len(df),
        "anomaly_count": int(df['is_anomaly'].sum()),
        "anomaly_rate": float(df['is_anomaly'].mean())
    }
    
    return df, params, anomaly_stats

def create_windows(df):
    """時系列ウィンドウ (X) と予測対象 (y) を作成"""
    data = df['Temperature'].values
    X, y = [], []
    for i in range(len(data) - WINDOW_SIZE):
        X.append(data[i:i + WINDOW_SIZE])
        y.append(data[i + WINDOW_SIZE])
    return np.array(X, dtype=np.float32), np.array(y, dtype=np.float32)

# ── メイン処理 ──────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="ONNX学習用データ前処理スクリプト")
    parser.add_argument("--logistics", type=str, default="smart_logistics_dataset.csv", help="Kaggle物流CSVのパス")
    parser.add_argument("--noaa", type=str, default="4286573.csv", help="NOAA気象CSVのパス")
    parser.add_argument("--station", type=str, default="USW00024233", help="Seattle: USW00024233, Yokohama: JA000047670")
    args = parser.parse_args()

    if not os.path.exists(OUTPUT_DIR):
        os.makedirs(OUTPUT_DIR)

    # 1. データマージ
    try:
        df_merged = prepare_merged_data(args.logistics, args.noaa, args.station)
    except Exception as e:
        print(f"[Error] {e}")
        return

    # 2. 異常注入
    df_with_anomalies = inject_anomalies(df_merged)

    # 3. 正規化
    df_norm, scaler_params, anomaly_stats = normalize_features(df_with_anomalies)

    # 4. ウィンドウ作成
    X, y = create_windows(df_norm)

    # 5. 保存
    np.save(os.path.join(OUTPUT_DIR, "X.npy"), X)
    np.save(os.path.join(OUTPUT_DIR, "y.npy"), y)
    
    # スケーラーパラメータ保存
    with open(os.path.join(OUTPUT_DIR, "scaler_params.json"), "w") as f:
        json.dump(scaler_params, f, indent=2)

    # 統計情報保存
    with open(os.path.join(OUTPUT_DIR, "anomaly_stats.json"), "w") as f:
        json.dump(anomaly_stats, f, indent=2)

    df_norm.to_csv(os.path.join(OUTPUT_DIR, "features.csv"), index=False)
    
    print("\n=== 前処理完了 ===")
    print(f"地点ID: {args.station}")
    print(f"保存先: {OUTPUT_DIR}/")
    print(f"データ行数: {len(X)}")

if __name__ == "__main__":
    main()