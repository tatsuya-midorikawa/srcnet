---
name: dbg-research-connector
description: "Search and browse code repositories via direct MCP communication with reSearch server. USE WHEN: user asks to search code across repos, compare file versions, batch multiple repository queries, build custom code search pipelines, or investigate file history without per-call MCP tool overhead. DO NOT USE FOR: local workspace file search (use grep_search/semantic_search instead)."
argument-hint: "Describe what to search for, which repos/branches, or the pipeline of operations"
---

# reSearch Direct MCP Client

Communicate directly with the reSearch MCP server via JSON-RPC over stdio, bypassing VS Code's MCP tool layer. This enables batched operations, custom pipelines, and flexible code search workflows in a single server session.

## When to Use

- Batch multiple code search queries in a single server session
- Chain operations: discover repos → list branches → search code → read content
- Build custom filtering pipelines over search results
- Investigate file change history across branches
- Any scenario where multiple `mcp_research_*` tool calls would be needed sequentially

## Available Actions

| Action | Purpose | Required Params |
|--------|---------|-----------------|
| `list-tools` | Discover available MCP tools | (none) |
| `repos` | List repositories | `-RepoPattern` (optional) |
| `branches` | List branches in a repo | `-RepoName` |
| `search` | Search code by query | `-SearchScope`, `-Query` |
| `content` | Read file content (single page) | `-SearchScope`, `-FilePath` |
| `content-all` | Read entire file (auto-paging) | `-SearchScope`, `-FilePath` |
| `history` | Get file change history | `-SearchScope`, `-FilePath` |

## OS Version to Branch Mapping

The user's question should include an OS version. Use the table below to determine the correct SearchScope without needing `repos` or `branches` lookups.

| OS Version | SearchScope |
|---|---|
| Windows Vista / Server 2008 | `Windows_Vista_Sd (SD)\vistasp2_ldr` |
| Windows 7 / Server 2008 R2 | `Windows_7_Sd (SD)\win7sp1_ldr` |
| Windows 8 / Server 2012 | `Windows_8_Sd (SD)\win8_ldr` |
| Windows 8.1 / Server 2012 R2 | `Blue_Sd (SD)\winblue_ltsb` |
| Windows 10 RS1 / Server 2016 | `Os.2020 (Git)\rs1_release_inmarket (official)` |
| Windows 10 RS5 / Server 2019 | `Os.2020 (Git)\rs5_release_svc_im (official)` |
| Windows 10 22H2 | `Os.2020 (Git)\vb_release_svc_im (official)` |
| Windows 10 | `Os.2020 (Git)\vb_release_svc_im (official)` |
| Windows Server 2022 | `Os.2020 (Git)\fe_release_svc_im (official)` |
| Windows 11 22H2 / 23H2 | `Os.2020 (Git)\ni_release_svc_im (official)` |
| Windows Server 2025 | `Os.2020 (Git)\lt_release_svc_im (official)` |
| Windows 11 24H2 / 25H2 | `Os.2020 (Git)\ge_release_svc_im (official)` |
| Windows 11 26H1 | `Os.2020 (Git)\br_release_svc_im (official)` |
| Windows 11 | `Os.2020 (Git)\br_release_svc_im (official)` |
| **(未指定)** | `Os.2020 (Git)\main (official)` |

When an OS version is specified, skip Step 1 and use the SearchScope directly in Step 2.

## Procedure

### Step 1: Discover Repos and Branches

Skip this step if the OS version is specified (use the mapping table above). Otherwise, discover available repositories:

```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" repos
```

Then get branches:

```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" branches -RepoName "RepoName (Git)"
```

### Step 2: Run Operations

**SearchScope format**: `"RepoName\BranchId"` — use the repo `name` from `repos` and the branch `id` from `branches` (e.g. `"Os.2020 (Git)\main (official)"`).

#### Single search:
```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" search `
    -SearchScope "Os.2020 (Git)\main (official)" `
    -Query "func:CreateFile" `
    -MaxResults 20
```

#### Read file content (single page):
```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" content `
    -SearchScope "Os.2020 (Git)\main (official)" `
    -FilePath "src/kernel/file.c" `
    -CharOffset 0 -Length 10000
```

#### Read entire file (auto-paging):
```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" content-all `
    -SearchScope "Os.2020 (Git)\main (official)" `
    -FilePath "src/kernel/file.c" `
    -MaxTotalLength 200000
```

Returns `{ filePath, totalLength, truncated, text }`. Default page size is 50000 chars; override with `-Length`. Default max total is 500000 chars; override with `-MaxTotalLength`.

#### Get file history:
```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" history `
    -SearchScope "Os.2020 (Git)\main (official)" `
    -FilePath "src/kernel/file.c"
```

### Step 3: Pipeline Mode (Batched Operations)

For multi-step workflows, use `-PipelineJson` to execute them all in a single MCP session.

#### Template References

Pipeline steps can reference results from previous steps using `{{$N.dotpath}}` syntax:
- `{{$0.repositories.0.name}}` → first repo name from step 0
- `{{$1.branches.0.id}}` → first branch ID from step 1
- Numeric path segments are array indices: `repositories.3.name` = 4th repo's name

#### Auto-constructed SearchScope example:
```powershell
& ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" `
    -PipelineJson '[
        {"action":"repos","repoPattern":"Os.2020"},
        {"action":"branches","repoName":"{{$0.repositories.0.name}}","branchPattern":"main","maxBranchCount":1},
        {"action":"search","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","query":"CreateFile","maxResults":5}
    ]'
```

Step 2 automatically constructs `"Os.2020 (Git)\main (official)"` from the repo name (step 0) and branch ID (step 1).

#### Pipeline with auto-paging content:
```json
[
    {"action":"repos","repoPattern":"Os.2020"},
    {"action":"branches","repoName":"{{$0.repositories.0.name}}","branchPattern":"main","maxBranchCount":1},
    {"action":"search","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","query":"NtCreateFile","maxResults":1},
    {"action":"content-all","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","filePath":"{{$2.results.0.filePath}}","maxTotalLength":100000}
]
```

Pipeline JSON schema per step:

```json
{
    "action": "repos|branches|search|content|content-all|history",
    "repoName": "string",
    "repoPattern": "string",
    "branchPattern": "string",
    "searchScope": "RepoName\\Branch (supports {{$N.path}} refs)",
    "query": "search string",
    "filePath": "relative/path",
    "charOffset": 0,
    "length": 5000,
    "maxTotalLength": 500000,
    "revisionId": "string",
    "maxResults": 10,
    "maxHits": 0,
    "skip": 0,
    "hitSkip": 0,
    "maxHistoryCount": 50,
    "noStitchedHistory": false
}
```

Pipeline output is a JSON array of `{ step, action, ok, result }` objects.

## Search Query Syntax

The `search` action's `-Query` parameter supports:

| Syntax | Example | Description |
|--------|---------|-------------|
| plain text | `CreateFile` | Full-text search |
| `func:` | `func:CreateFile` | Function/method name |
| `type:` | `type:FileStream` | Type/class name |
| `file:` | `file:*.cs` | File name pattern |
| `path:` | `path:src/kernel/` | Path pattern |
| `AND` / `OR` | `CreateFile AND kernel` | Boolean operators |
| `"quoted"` | `"exact phrase"` | Exact phrase match |

Refer to your reSearch server documentation for the full query syntax.

## Output Format

All output is JSON. Parse it to extract specific fields:

```powershell
$result = & ".\.github\skills\dbg-research-connector\scripts\research-client.ps1" repos | ConvertFrom-Json
$result | Select-Object -Property name
```

## Report Template

調査結果は以下のテンプレートに従って構造化して報告すること。

```markdown
# 🔍 Windows ソースコード 調査報告書

## 調査対象
{調査対象の機能名、API、コンポーネント、ユーザーからの問い合わせ内容}

## 調査条件

| 項目 | 値 |
|------|-----|
| OS バージョン | {対象 OS バージョン} |
| SearchScope | {使用した SearchScope} |
| 検索クエリ | {使用したクエリ} |

## 調査結果

### 概要
{調査結果の概要を 2-3 文で簡潔に記載}

### 動作仕様

#### 通常フロー
{機能の正常系の動作フローを記述}

```sample
タッチペン入力
  → HID ドライバー (hidi2c / hidSpi)
    → Windows Input Stack (InputHost.exe / win32kfull.sys)
      → WM_POINTERDOWN / WM_POINTERUPDATE / WM_POINTERUP
        → MSHTML (IE モード Trident エンジン)
          → MSPointerDown / MSPointerMove / MSPointerUp (JavaScript)
            → Canvas/SVG に署名を描画
```

#### 条件分岐
{Feature Flag、レジストリ設定、グループポリシー、OS エディション等による動作の分岐を記述}

| 条件 | 動作 |
|------|------|
| {条件 1} | {対応する動作} |
| {条件 2} | {対応する動作} |

#### OS バージョン間の差異
{複数バージョンを調査した場合、バージョン間の仕様差異を記述}

### 関連コードの参照箇所
{主要なファイルパスと関数名を列挙}

- `path/to/file.c` — {関数名}: {役割の簡潔な説明}
- `path/to/file.h` — {構造体/定義名}: {役割の簡潔な説明}

## 根拠
{調査結果の根拠を記載。コードの具体的な箇所、変更履歴、
関連するリビジョン ID 等を含める}

## 補足
{追加の注意事項、関連する既知の問題、今後の変更予定等があれば記載}
```

### Important Rules

- **回答は日本語で行うこと**
- **誤った情報を提供しないこと** — 不確実な内容には必ずその旨を明記する
- **事実と推測を区別すること** — コードから読み取れる事実と、推測・解釈を明確に分ける
- **根拠を必ず添えること** — ファイルパス、コード行、リビジョン ID など具体的な参照を含める

## Error Handling

- If the server path is wrong, the script reads from `%APPDATA%\Code\User\mcp.json` automatically
- Tool names are auto-discovered on session start (handles both snake_case and PascalCase)
- Each pipeline step reports `ok: true/false` independently — one failure doesn't stop the batch
- On error, output is `{ "ok": false, "error": "message" }`

## Common Pipelines

### Auto-discover and search
```json
[
    {"action":"repos","repoPattern":"MyRepo"},
    {"action":"branches","repoName":"{{$0.repositories.0.name}}","branchPattern":"main","maxBranchCount":1},
    {"action":"search","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","query":"MyClass","maxResults":5}
]
```

### Cross-repo search
```json
[
    {"action":"repos"},
    {"action":"search","searchScope":"Repo1 (Git)\\main (official)","query":"MyClass","maxResults":5},
    {"action":"search","searchScope":"Repo2 (Git)\\develop (official)","query":"MyClass","maxResults":5}
]
```

### File investigation (auto-paging)
```json
[
    {"action":"repos","repoPattern":"MyRepo"},
    {"action":"branches","repoName":"{{$0.repositories.0.name}}","branchPattern":"main","maxBranchCount":1},
    {"action":"search","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","query":"file:config.yaml","maxResults":1},
    {"action":"content-all","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","filePath":"{{$2.results.0.filePath}}","maxTotalLength":100000},
    {"action":"history","searchScope":"{{$0.repositories.0.name}}\\{{$1.branches.0.id}}","filePath":"{{$2.results.0.filePath}}","maxHistoryCount":10}
]
```
