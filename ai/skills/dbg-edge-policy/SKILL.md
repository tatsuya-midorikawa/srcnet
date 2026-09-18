---
name: dbg-edge-policy
description: 'Microsoft Edge の policies.json を解析するスキル。policies.json のパース、ポリシー一覧の表示、各ポリシーの詳細確認、設定値の分析を行う。Use when: Edge ポリシー、policies.json、ブラウザポリシー、Edge 設定、GPO ポリシー、Intune ポリシー、Edge Update ポリシーの解析・確認が必要な場合。'
argument-hint: 'policies.json のパスを指定、または現在のファイルを使用'
---

# Edge Policy Analyzer

Microsoft Edge の `policies.json` を解析し、適用されているポリシーの一覧・詳細・設定値を確認するスキル。

## policies.json の構造

`policies.json` は Microsoft Edge が適用しているポリシー情報をエクスポートしたファイル。`edge://policy` からエクスポートできる。

```
{
  "chromeMetadata": { ... },    // Edge のバージョン・OS 情報
  "policyValues": {
    "chrome": {                  // Edge ブラウザポリシー
      "name": "Microsoft Edge Policies",
      "policies": { ... }
    },
    "extensions": { ... },       // 拡張機能ポリシー
    "precedence": { ... },       // ポリシー優先度
    "updater": {                 // Edge Update ポリシー
      "name": "Microsoft EdgeUpdate Policies",
      "policies": { ... }
    }
  },
  "status": { ... }             // ステータス情報
}
```

### 各ポリシーのプロパティ

| プロパティ | 説明 |
|-----------|------|
| `level` | `mandatory` (必須) または `recommended` (推奨) |
| `scope` | `machine` (マシン全体) または `user` (ユーザー単位) |
| `source` | `platform` (GPO/レジストリ)、`cloud` (Intune/クラウド)、`enterprise_default` (既定) |
| `value` | ポリシーの設定値 (型はポリシーにより異なる: bool, int, string, list, dict) |

## 手順

### Step 1: ファイルの読み込みと基本情報の表示

`policies.json` を読み込み、以下の基本情報を表示する:

1. **Edge バージョン情報**: `chromeMetadata` から OS、バージョン、ビルドリビジョンを確認
2. **ポリシーセクション一覧**: `policyValues` の各セクション (`chrome`, `extensions`, `precedence`, `updater`) のポリシー数を確認
3. **ステータス情報**: `status` から更新タイミング等を確認

### Step 2: ポリシー一覧の作成

各セクションのポリシーを一覧で表示する。以下の形式でまとめる:

#### ブラウザポリシー (`policyValues.chrome.policies`)

| ポリシー名 | Level | Scope | Source | 値の型 | ドキュメント |
|-----------|-------|-------|--------|--------|------------|
| {policy-name} | {level} | {scope} | {source} | {type} | [詳細](https://learn.microsoft.com/ja-jp/deployedge/microsoft-edge-browser-policies/{policy-name-lowercase}) |

#### Update ポリシー (`policyValues.updater.policies`)

| ポリシー名 | Level | Scope | Source | 値の型 | ドキュメント |
|-----------|-------|-------|--------|--------|------------|
| {policy-name} | {level} | {scope} | {source} | {type} | [詳細](https://learn.microsoft.com/ja-jp/deployedge/microsoft-edge-update-policies#{policy-name-lowercase}) |

### Step 3: ポリシーの詳細確認

特定のポリシーの詳細を確認する場合:

1. **ブラウザポリシー**: `fetch_webpage` ツールを使用して以下の URL から詳細を取得する
   - URL: `https://learn.microsoft.com/ja-jp/deployedge/microsoft-edge-browser-policies/{ポリシー名を小文字に変換}`
   - 例: `BlockExternalExtensions` → `https://learn.microsoft.com/ja-jp/deployedge/microsoft-edge-browser-policies/blockexternalextensions`

2. **Update ポリシー**: `fetch_webpage` ツールを使用して以下の URL から詳細を取得する
   - URL: `https://learn.microsoft.com/ja-jp/deployedge/microsoft-edge-update-policies#{ポリシー名を小文字に変換}`
   - 例: `TargetChannel` → `https://learn.microsoft.com/ja-jp/deployedge/microsoft-edge-update-policies#targetchannel`

3. 取得した情報から以下をまとめる:
   - ポリシーの説明
   - サポートされるバージョン
   - 設定可能な値とその意味
   - 現在の設定値との比較

### Step 4: 設定値の分析

ポリシーの設定値を分析する際のチェックポイント:

1. **セキュリティ関連ポリシー**: SmartScreen 系、拡張機能ブロック系の設定が適切か
2. **拡張機能管理**: `ExtensionInstallBlocklist`, `ExtensionInstallForcelist`, `ExtensionSettings` の整合性
3. **mandatory vs recommended**: 必須ポリシーと推奨ポリシーの区別
4. **source の確認**: GPO (platform) とクラウド (cloud) の競合がないか

### Step 5: レポート出力 (オプション)

必要に応じて、分析結果をまとめたレポートを作成する:

```markdown
# Edge ポリシー分析レポート

## 環境情報
- OS: {OS}
- Edge バージョン: {version}

## ポリシーサマリー
- ブラウザポリシー: {count} 件
- Update ポリシー: {count} 件

## ポリシー詳細
### {policy-name}
- 説明: ...
- 設定値: ...
- ドキュメント: [リンク]

## セキュリティ評価
- ...
```

## 分析の観点

ユーザーが特定の観点で分析を求めた場合の対応:

| 分析観点 | 確認するポリシー |
|---------|----------------|
| セキュリティ | SmartScreenEnabled, SmartScreenPuaEnabled, SmartScreenForTrustedDownloadsEnabled, BlockExternalExtensions |
| 拡張機能管理 | ExtensionInstallBlocklist, ExtensionInstallForcelist, ExtensionSettings, ExtensionInstallSources |
| ネットワーク | URLBlocklist, LocalNetworkAccessAllowedForUrls |
| IE モード | InternetExplorerIntegrationLevel, InternetExplorerIntegrationCloudSiteList |
| 同期設定 | SyncTypesListDisabled |
| 印刷設定 | PrintStickySettings, UseSystemPrintDialog |
| 更新管理 | EdgePreview, TargetChannel, UpdaterExperimentationAndConfigurationServiceControl |

## 注意事項

- `ExtensionSettings` の値が大量にある場合は、`installation_mode` ごとにグループ化して表示する
- `value` が配列の場合は件数を表示し、必要に応じて内容を展開する
- ドキュメント URL のポリシー名部分は小文字に変換する
- `fetch_webpage` でドキュメントを取得する際は、該当ポリシー名をクエリとして指定する
