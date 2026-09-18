---
name: dbg-net-export
description: 'Chromium/Edge の net-export ログ (JSON) を解析するスキル。chrome://net-export や edge://net-export で取得したネットワークログのパース、イベント分析、DNS・HTTP・QUIC・TLS の通信解析、エラー診断、タイムライン分析を行う。Use when: net-export、NetLog、ネットワークログ、通信ログ、HTTP エラー、DNS 解決、QUIC セッション、TLS ハンドシェイク、Cookie、プロキシ、ソケット、キャッシュ、ネットワーク診断・トラブルシューティングが必要な場合。'
argument-hint: 'net-export ログ JSON ファイルのパス、または現在のファイルを使用'
---

# Net-Export Log Analyzer

Chromium/Edge の `net-export` (NetLog) JSON ファイルを解析し、ネットワーク通信の詳細な分析・診断を行うスキル。
[netlog_viewer](https://chromium.googlesource.com/catapult/+/master/netlog_viewer/) の解析ロジックに基づく。

## net-export ログの構造

`net-export` ログは `chrome://net-export` または `edge://net-export` からエクスポートされる JSON ファイル。

```
{
  "constants": {          // 定数定義 (イベント型、ソース型、エラーコード等)
    "clientInfo": { ... },      // ブラウザ情報 (名前、バージョン、OS、コマンドライン)
    "logEventTypes": { ... },   // イベント型の名前→数値マッピング (600+種類)
    "logSourceType": { ... },   // ソース型の名前→数値マッピング (50+種類)
    "logEventPhase": { ... },   // フェーズ (PHASE_BEGIN=0, PHASE_END=1, PHASE_NONE=2)
    "logFormatVersion": 1,      // ログフォーマットバージョン
    "timeTickOffset": <number>, // TimeTicks → UTC 変換用オフセット (ミリ秒)
    "netError": { ... },        // ネットワークエラーコード
    "loadFlag": { ... },        // ロードフラグ
    "loadState": { ... },       // ロード状態
    "addressFamily": { ... },   // アドレスファミリ
    "certStatusFlag": { ... },  // 証明書ステータスフラグ
    "quicError": { ... },       // QUIC エラーコード
    "activeFieldTrialGroups": [ ... ] // アクティブな実験グループ
  },
  "events": [             // イベントの配列 (数万〜数十万件)
    {
      "type": <number>,         // イベント型 (logEventTypes の値)
      "phase": <number>,        // フェーズ (0=BEGIN, 1=END, 2=NONE)
      "source": {
        "id": <number>,         // ソース ID (同一ソースのイベントをグループ化)
        "type": <number>,       // ソース型 (logSourceType の値)
        "start_time": "<ticks>" // ソースの開始 TimeTicks
      },
      "time": "<ticks>",        // イベント発生 TimeTicks
      "params": { ... }         // イベント固有パラメータ (オプション)
    }
  ],
  "polledData": {          // キャプチャ時のスナップショットデータ
    "hostResolverInfo": { ... },    // DNS キャッシュ・設定
    "proxySettings": { ... },      // プロキシ設定
    "quicInfo": { ... },           // QUIC 設定
    "spdySessionInfo": [ ... ],    // HTTP/2 セッション情報
    "altSvcMappings": [ ... ],     // Alternative Service マッピング
    "socketPoolInfo": [ ... ],     // ソケットプール情報
    "httpCacheInfo": { ... },      // HTTP キャッシュ情報
    "httpStreamPoolInfo": { ... }, // HTTP ストリームプール情報
    "extensionInfo": [ ... ],      // 拡張機能情報
    "reportingInfo": { ... },      // Reporting API 情報
    "prerenderInfo": { ... },      // プリレンダー情報
    "spdyStatus": { ... },        // HTTP/2 ステータス
    "serviceProviders": { ... }    // サービスプロバイダー (Windows)
  }
}
```

### 時刻変換

イベントの `time` および `source.start_time` は **TimeTicks** (文字列) で記録される。UTC ミリ秒に変換するには:

```
UTC ミリ秒 = timeTickOffset + TimeTicks
Date = new Date(timeTickOffset + Number(timeTicks))
```

### ソース型 (Source Types)

イベントは **ソース** でグループ化される。同じ `source.id` を持つイベントは同一の通信単位に属する。

| ソース型 | 説明 | description の取得元 |
|---------|------|---------------------|
| `URL_REQUEST` (1) | HTTP/HTTPS リクエスト | `params.url` |
| `SOCKET` (10) | TCP ソケット | 親ソースの description |
| `HTTP2_SESSION` (11) | HTTP/2 セッション | `params.host` + `params.proxy` |
| `QUIC_SESSION` (13) | QUIC セッション | `params.host` |
| `HOST_RESOLVER_IMPL_JOB` (14) | DNS 解決ジョブ | `params.host` |
| `DISK_CACHE_ENTRY` (15) | ディスクキャッシュエントリ | `params.key` |
| `HTTP_STREAM_JOB` (17) | HTTP ストリームジョブ | `params.url` |
| `UDP_SOCKET` (24) | UDP ソケット | `params.address` |
| `CERT_VERIFIER_JOB` (26) | 証明書検証ジョブ | `params.host` |
| `SSL_CONNECT_JOB` (5) | TLS 接続ジョブ | `params.group_id` |
| `TRANSPORT_CONNECT_JOB` (6) | TCP 接続ジョブ | `params.group_id` |
| `COOKIE_STORE` (38) | Cookie ストア | - |
| `DNS_TRANSACTION` (44) | DNS トランザクション | `params.hostname` |
| `DNS_OVER_HTTPS` (43) | DoH リクエスト | `params.hostname` |
| `HTTP_STREAM_JOB_CONTROLLER` (32) | ストリームジョブコントローラー | - |
| `NETWORK_QUALITY_ESTIMATOR` (31) | ネットワーク品質推定 | - |

## 手順

### Step 1: ファイルの読み込みと基本情報の表示

net-export ログ JSON ファイルを読み込み、以下の基本情報を表示する:

1. **ブラウザ情報** (`constants.clientInfo`):
   - ブラウザ名、バージョン、ビルド
   - OS 種別・バージョン
   - コマンドライン引数
2. **ログ情報**:
   - `logCaptureMode` (キャプチャモード: `Default`, `IncludeSensitive`, `Everything`)
   - イベント総数 (`events.length`)
   - 定義済みイベント型数 (`logEventTypes` のキー数)
   - 定義済みソース型数 (`logSourceType` のキー数)
3. **時刻範囲**: 最初と最後のイベントの TimeTicks を UTC に変換して表示
4. **polledData の概要**: 各セクションの有無とサイズ

### Step 2: イベントのデコードとサマリー

`constants` の逆引きマップを構築し、イベントを人間が読める形式にデコードする:

1. **逆引きマップの構築**:
   - `logEventTypes` → 数値からイベント型名への逆引き
   - `logSourceType` → 数値からソース型名への逆引き
   - `logEventPhase` → 数値からフェーズ名への逆引き
   - `netError` → 数値からエラー名への逆引き

2. **ソース型別イベント集計**: 各ソース型ごとのイベント数を集計

3. **イベント型別集計 (上位)**: 頻出イベント型を上位 20〜30 件表示

4. **エラーイベントの抽出**: `params.net_error` が 0 以外のイベントを抽出し、エラーコード名と共に表示

### Step 3: URL リクエストの分析

`URL_REQUEST` ソース型のイベントをグループ化し、リクエスト単位で分析する:

1. **ソースIDでグループ化**: 同一 `source.id` のイベントをまとめる
2. **リクエスト一覧**: 各リクエストの URL (`URL_REQUEST_START_JOB` の `params.url`)、メソッド、ステータスコードを表示
3. **タイムライン**: 各リクエストの開始時刻・終了時刻・所要時間を計算
4. **エラーリクエスト**: `net_error` を含むリクエストを強調表示

リクエストのライフサイクルイベント:
```
REQUEST_ALIVE (BEGIN)
  → URL_REQUEST_START_JOB (BEGIN)
    → NETWORK_DELEGATE_BEFORE_URL_REQUEST (BEGIN/END)
    → FIRST_PARTY_SETS_METADATA (BEGIN/END)
    → NETWORK_DELEGATE_BEFORE_START_TRANSACTION (BEGIN/END)
    → HTTP_CACHE_GET_BACKEND (BEGIN/END)
    → HTTP_CACHE_OPEN_OR_CREATE_ENTRY (BEGIN/END)
    → HTTP_CACHE_ADD_TO_ENTRY (BEGIN/END)
    → HTTP_STREAM_JOB_CONTROLLER (BEGIN)
      → HTTP_STREAM_JOB (BEGIN)
        → HTTP_STREAM_JOB_INIT_CONNECTION (BEGIN/END)
      → HTTP_STREAM_JOB (END)
    → URL_REQUEST_DELEGATE_CONNECTED (BEGIN/END)
    → NETWORK_DELEGATE_HEADERS_RECEIVED (BEGIN/END)
    → URL_REQUEST_DELEGATE_RESPONSE_STARTED (BEGIN/END)
    → HTTP_TRANSACTION_READ_BODY (BEGIN/END)
    → HTTP_CACHE_READ_DATA / HTTP_CACHE_WRITE_DATA
  → URL_REQUEST_START_JOB (END)
REQUEST_ALIVE (END)
```

### Step 4: DNS 分析

DNS 関連の情報を分析する:

1. **DNS キャッシュ** (`polledData.hostResolverInfo.cache`):
   - キャッシュ容量、エントリ数
   - 各エントリ: ホスト名、アドレスファミリ、解決アドレス、TTL、有効期限
   - 期限切れエントリの特定

2. **DNS 設定** (`polledData.hostResolverInfo.dns_config`):
   - ネームサーバー一覧
   - DoH (DNS over HTTPS) 設定
   - 安全な DNS トランザクション可否

3. **DNS 解決イベント** (`HOST_RESOLVER_IMPL_JOB`, `DNS_TRANSACTION` ソース):
   - 解決対象ホスト名
   - 解決時間
   - キャッシュヒット/ミス (`HOST_RESOLVER_MANAGER_CACHE_HIT`)
   - エラー (NXDOMAIN 等)

4. **DoH イベント** (`DNS_OVER_HTTPS` ソース):
   - DoH クエリの対象ホスト
   - 使用している DoH プロバイダー

### Step 5: HTTP/2 (SPDY) セッション分析

`polledData.spdySessionInfo` と `HTTP2_SESSION` ソースのイベントを分析:

1. **アクティブセッション一覧**:
   - ホスト:ポート、プロトコル
   - アクティブストリーム数、作成済みストリーム数
   - ウィンドウサイズ (送信/受信)
   - フレーム受信数
   - エラー状態

2. **セッションイベント**:
   - `HTTP2_SESSION_SEND_HEADERS` / `HTTP2_SESSION_RECV_HEADERS`: ヘッダー送受信
   - `HTTP2_SESSION_SEND_DATA` / `HTTP2_SESSION_RECV_DATA`: データ送受信
   - `HTTP2_SESSION_GOAWAY`: GOAWAY フレーム
   - `HTTP2_SESSION_RST_STREAM`: ストリームリセット

### Step 6: QUIC セッション分析

`polledData.quicInfo` と `QUIC_SESSION` ソースのイベントを分析:

1. **QUIC 設定** (`polledData.quicInfo`):
   - 接続オプション
   - アイドルタイムアウト
   - マイグレーション設定
   - 初期 RTT

2. **QUIC セッションイベント**:
   - `QUIC_SESSION_PACKET_RECEIVED` / `QUIC_SESSION_PACKET_SENT`: パケット送受信
   - `QUIC_SESSION_STREAM_FRAME_RECEIVED`: ストリームフレーム受信
   - `QUIC_SESSION_CRYPTO_HANDSHAKE_MESSAGE_RECEIVED`: ハンドシェイクメッセージ
   - `QUIC_SESSION_CONNECTION_CLOSE_FRAME_RECEIVED`: 接続クローズ
   - `QUIC_SESSION_UNAUTHENTICATED_PACKET_HEADER_RECEIVED`: 未認証パケットヘッダー

3. **QUIC エラー**: `quicError`, `quicRstStreamError` を使ってエラーコードを名前に変換

### Step 7: TLS/SSL 分析

SSL 接続ジョブ (`SSL_CONNECT_JOB`) と証明書検証 (`CERT_VERIFIER_JOB`, `CERT_VERIFIER_TASK`) を分析:

1. **TLS ハンドシェイク**:
   - 接続先ホスト
   - ハンドシェイク所要時間
   - ネゴシエートされたプロトコル

2. **証明書検証**:
   - 検証対象ホスト
   - 検証結果 (`certStatusFlag`)
   - 検証エラー

### Step 8: プロキシ分析

`polledData.proxySettings` と プロキシ関連イベントを分析:

1. **プロキシ設定**:
   - `original`: ユーザー/管理者が設定したプロキシ
   - `effective`: 実際に適用されているプロキシ
2. **不良プロキシ** (`polledData.badProxies`): 一時的にブロックされたプロキシ一覧

### Step 9: ソケット・接続分析

`polledData.socketPoolInfo` と `SOCKET`, `TRANSPORT_CONNECT_JOB` ソースのイベントを分析:

1. **ソケットプール情報**:
   - 最大ソケット数
   - アイドル/ハンドアウト/接続中のソケット数
   - グループごとの接続状態

2. **接続ジョブ**:
   - TCP 接続ジョブの所要時間
   - SSL 接続ジョブの所要時間
   - 接続エラー

### Step 10: Cookie 分析

`COOKIE_STORE` ソースの `COOKIE_INCLUSION_STATUS` イベントを分析:

1. **Cookie 操作**: `params` から Cookie 名、ドメイン、操作 (取得/設定) を確認
2. **除外理由**: Cookie が除外された場合の理由 (`exclusion_reason`)
3. **SameSite 属性**: SameSite ポリシーによる影響

### Step 11: キャッシュ分析

`polledData.httpCacheInfo` と `DISK_CACHE_ENTRY` ソースのイベントを分析:

1. **キャッシュ統計**: サイズ、エントリ数
2. **キャッシュヒット/ミス**: `HTTP_CACHE_OPEN_OR_CREATE_ENTRY` の結果
3. **キャッシュ読み書き**: `ENTRY_READ_DATA`, `ENTRY_WRITE_DATA`

### Step 12: Alternative Service 分析

`polledData.altSvcMappings` を分析:

1. **Alt-Svc マッピング一覧**: サーバーと代替サービス (QUIC, HTTP/2) のマッピング
2. **プロトコルアップグレード**: HTTP/1.1 → HTTP/2 → HTTP/3 (QUIC) の状況

### Step 13: 拡張機能分析

`polledData.extensionInfo` を分析:

1. **インストール済み拡張機能一覧**: 名前、ID、バージョン、有効/無効
2. **ネットワークに影響する拡張機能**: WebRequest API を使用する拡張機能の特定

## 分析の観点

ユーザーが特定の観点で分析を求めた場合の対応:

| 分析観点 | 確認する項目 |
|---------|------------|
| 接続エラー | `net_error` を含むイベント、エラーコード名への変換 (`constants.netError`) |
| 遅延・パフォーマンス | リクエストの所要時間、DNS 解決時間、TLS ハンドシェイク時間、TTFB |
| DNS 問題 | DNS キャッシュ、DNS 解決イベント、NXDOMAIN、DoH 設定 |
| TLS/証明書エラー | 証明書検証結果、ハンドシェイク失敗、`certStatusFlag` |
| QUIC 問題 | QUIC セッションエラー、接続マイグレーション、ハンドシェイク失敗 |
| プロキシ問題 | プロキシ設定、不良プロキシ、プロキシ接続エラー |
| Cookie 問題 | Cookie 除外理由、SameSite、Secure 属性 |
| キャッシュ効率 | キャッシュヒット率、キャッシュサイズ |
| 特定 URL の調査 | その URL の `URL_REQUEST` ソースのイベントをフィルタリング |
| HTTP/2 問題 | HTTP/2 セッションエラー、GOAWAY、RST_STREAM |
| 拡張機能の影響 | ネットワーク関連拡張機能の特定 |

## レポート出力 (オプション)

必要に応じて、分析結果をまとめたレポートを作成する:

```markdown
# Net-Export ログ分析レポート

## 環境情報
- ブラウザ: {name} {version} ({version_mod})
- OS: {os_type}
- キャプチャモード: {logCaptureMode}

## ログサマリー
- イベント数: {total_events} 件
- 時間範囲: {start_time} 〜 {end_time} ({duration})
- ソース型数: {source_type_count}
- エラーイベント数: {error_count}

## URL リクエストサマリー
| URL | メソッド | ステータス | 所要時間 | エラー |
|-----|---------|-----------|---------|-------|
| ... | ... | ... | ... | ... |

## DNS 分析
- キャッシュエントリ数: {cache_count}
- ...

## エラー一覧
| 時刻 | ソース型 | イベント型 | エラーコード | 詳細 |
|------|---------|-----------|------------|------|
| ... | ... | ... | ... | ... |

## 診断結果
- ...
```

## 解析スクリプト

大きなログファイルの効率的な処理のため、以下の Python スクリプトを同梱している。`run_in_terminal` で実行する。

### [summary.py](./scripts/summary.py) - ログサマリー

```bash
python3 .github/skills/dbg-net-export/scripts/summary.py <net-export-log.json>
```

ブラウザ情報、イベント統計、ソース型/イベント型分布、エラー集計、polledData 概要を一括表示。

### [url_requests.py](./scripts/url_requests.py) - URL リクエスト分析

```bash
python3 .github/skills/dbg-net-export/scripts/url_requests.py <net-export-log.json> [--filter <keyword>] [--errors-only] [--top <N>] [--slow <ms>]
```

| オプション | 説明 |
|-----------|------|
| `--filter` / `-f` | URL キーワードでフィルタ |
| `--errors-only` / `-e` | エラーリクエストのみ |
| `--top` / `-t` | 表示件数 (デフォルト: 50) |
| `--slow` / `-s` | 指定ミリ秒以上の遅いリクエストのみ |

### [dns_analysis.py](./scripts/dns_analysis.py) - DNS 分析

```bash
python3 .github/skills/dbg-net-export/scripts/dns_analysis.py <net-export-log.json> [--filter <hostname>]
```

DNS キャッシュ、DNS 設定、ネームサーバー、DoH 設定、DNS 解決イベント、キャッシュヒット率、DNS エラーを分析。

### [errors.py](./scripts/errors.py) - エラー分析

```bash
python3 .github/skills/dbg-net-export/scripts/errors.py <net-export-log.json> [--source-type <type>] [--error-code <code>] [--top <N>]
```

net_error、quic_error、quic_rst_stream_error を網羅的に抽出。ソース型・エラーコード別の集計と詳細表示。

### [trace_source.py](./scripts/trace_source.py) - ソーストレース

```bash
python3 .github/skills/dbg-net-export/scripts/trace_source.py <net-export-log.json> --id <source_id>
python3 .github/skills/dbg-net-export/scripts/trace_source.py <net-export-log.json> --url <keyword>
python3 .github/skills/dbg-net-export/scripts/trace_source.py <net-export-log.json> --host <hostname> [--follow]
```

netlog_viewer の Events タブ相当の詳細表示。特定のソースIDまたは URL/ホスト名でフィルタし、イベントを時系列・階層構造で表示。`--follow` で依存ソースも追跡。

## 注意事項

- イベント数が非常に多い場合 (10 万件超)、全件読み込みでメモリ制約に達する可能性がある。必要に応じてストリーミング的に処理するか、先頭/末尾の一部イベントのみ読み込む
- `logCaptureMode` が `Default` の場合、Cookie 値や POST ボディなどのセンシティブ情報は含まれない。`IncludeSensitive` または `Everything` で取得したログにはセンシティブ情報が含まれる可能性があるため取り扱いに注意
- TimeTicks の変換には `constants.timeTickOffset` を使用する。この値がない場合、正確な UTC 時刻への変換はできない
- `--log-net-log` で生成されたログは途中で切断される場合がある。末尾の JSON が不完全な場合、最後のカンマ以前を切り取って `]}` を追加してパースする
- ログファイルサイズが大きい場合 (数百 MB)、JSON 全体のパースに時間がかかる。`run_in_terminal` で Python スクリプトを使って必要な部分のみ抽出・分析することを推奨
