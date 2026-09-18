#Requires -Version 5.1
<#
.SYNOPSIS
    Direct MCP JSON-RPC 2.0 client for reSearch code search server.
.DESCRIPTION
    Communicates with reSearch MCP server via stdio, bypassing VS Code MCP tool layer.
    Enables batched operations and custom pipelines for code search workflows.
.EXAMPLE
    .\research-client.ps1 repos
.EXAMPLE
    .\research-client.ps1 search -SearchScope "Os.2020 (Git)\main" -Query "func:CreateFile"
.EXAMPLE
    .\research-client.ps1 -PipelineJson '[{"action":"repos"},{"action":"branches","repoName":"MyRepo"}]'
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('list-tools', 'repos', 'branches', 'search', 'content', 'content-all', 'history')]
    [string]$Action = 'list-tools',

    [string]$SearchScope,
    [string]$RepoName,
    [string]$RepoPattern,
    [string]$BranchPattern,
    [string]$Query,
    [string]$FilePath,
    [int]$CharOffset = 0,
    [int]$Length = 5000,
    [string]$RevisionId,
    [int]$MaxResults = 10,
    [int]$MaxHits = 0,
    [int]$Skip = 0,
    [int]$HitSkip = 0,
    [int]$MaxHistoryCount = 50,
    [int]$MaxTotalLength = 500000,
    [switch]$NoStitchedHistory,
    [string]$PipelineJson,
    [string]$McpExePath
)

$ErrorActionPreference = 'Stop'
$script:nextId = 0
$script:toolNameCache = @{}

# ── Resolve reSearch.exe path ────────────────────────────────────────────

function Resolve-ExePath {
    param([string]$Explicit)
    if ($Explicit -and (Test-Path $Explicit)) { return $Explicit }
    # Search for reSearch.exe in PATH
    $inPath = Get-Command 'reSearch.exe' -ErrorAction SilentlyContinue
    if ($inPath) { return $inPath.Source }
    # Search common install locations
    $searchRoots = @(
        "$env:LOCALAPPDATA\Apps"
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($root in $searchRoots) {
        $found = Get-ChildItem -Path $root -Filter 'reSearch.exe' -Recurse -ErrorAction SilentlyContinue -File | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    throw "reSearch.exe not found. Install reSearch or specify -McpExePath."
}

# ── MCP Process Management ──────────────────────────────────────────────

function Start-McpServer {
    param([string]$ExePath)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ExePath
    $psi.Arguments = 'mcpserver'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding  = [System.Text.Encoding]::UTF8

    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc -or $proc.HasExited) {
        throw 'Failed to start reSearch MCP server process.'
    }
    return $proc
}

function Stop-McpServer {
    param([System.Diagnostics.Process]$Proc)
    try { $Proc.StandardInput.Close() } catch {}
    if (-not $Proc.WaitForExit(3000)) {
        try { $Proc.Kill() } catch {}
    }
    $Proc.Dispose()
}

# ── JSON-RPC 2.0 Communication ──────────────────────────────────────────

function Send-JsonRpcRequest {
    param(
        [System.Diagnostics.Process]$Proc,
        [string]$Method,
        [hashtable]$Params = @{}
    )
    $script:nextId++
    $reqId = $script:nextId
    $msg = @{ jsonrpc = '2.0'; id = $reqId; method = $Method; params = $Params }
    $json = $msg | ConvertTo-Json -Depth 30 -Compress
    $Proc.StandardInput.WriteLine($json)
    $Proc.StandardInput.Flush()

    while ($true) {
        $line = $Proc.StandardOutput.ReadLine()
        if ($null -eq $line) { throw 'MCP server closed connection.' }
        $line = $line.Trim()
        if ($line -eq '') { continue }
        try { $resp = $line | ConvertFrom-Json } catch { continue }
        if ($resp.PSObject.Properties['id'] -and $resp.id -eq $reqId) {
            if ($resp.error) {
                throw "MCP error [$($resp.error.code)]: $($resp.error.message)"
            }
            return $resp.result
        }
    }
}

function Send-JsonRpcNotification {
    param(
        [System.Diagnostics.Process]$Proc,
        [string]$Method
    )
    $msg = @{ jsonrpc = '2.0'; method = $Method }
    $json = $msg | ConvertTo-Json -Depth 5 -Compress
    $Proc.StandardInput.WriteLine($json)
    $Proc.StandardInput.Flush()
}

# ── MCP Session Lifecycle ───────────────────────────────────────────────

function Initialize-Session {
    param([System.Diagnostics.Process]$Proc)
    $null = Send-JsonRpcRequest -Proc $Proc -Method 'initialize' -Params @{
        protocolVersion = '2024-11-05'
        capabilities    = @{}
        clientInfo      = @{ name = 'research-skill-client'; version = '1.0.0' }
    }
    Send-JsonRpcNotification -Proc $Proc -Method 'notifications/initialized'
}

function Get-ToolMap {
    param([System.Diagnostics.Process]$Proc)
    $list = Send-JsonRpcRequest -Proc $Proc -Method 'tools/list' -Params @{}
    $map = @{}
    foreach ($t in $list.tools) { $map[$t.name.ToLower()] = $t.name }
    return $map
}

function Resolve-ToolName {
    param([string]$Canonical)
    $lower = $Canonical.ToLower()
    if ($script:toolNameCache.ContainsKey($lower)) { return $script:toolNameCache[$lower] }
    # Try PascalCase conversion: get_codebase_repos -> GetCodebaseRepos
    $pascal = ($Canonical -split '_' | ForEach-Object {
        $_.Substring(0,1).ToUpper() + $_.Substring(1)
    }) -join ''
    $pascalLower = $pascal.ToLower()
    if ($script:toolNameCache.ContainsKey($pascalLower)) { return $script:toolNameCache[$pascalLower] }
    throw "Tool '$Canonical' not found on MCP server. Run 'list-tools' to see available tools."
}

function Invoke-Tool {
    param(
        [System.Diagnostics.Process]$Proc,
        [string]$ToolName,
        [hashtable]$Arguments = @{}
    )
    $resolved = Resolve-ToolName -Canonical $ToolName
    $result = Send-JsonRpcRequest -Proc $Proc -Method 'tools/call' -Params @{
        name      = $resolved
        arguments = $Arguments
    }
    if ($result.content) {
        $text = ($result.content |
            Where-Object { $_.type -eq 'text' } |
            ForEach-Object { $_.text }) -join "`n"
        try { return ($text | ConvertFrom-Json) } catch { return $text }
    }
    return $result
}

function Invoke-ToolText {
    param(
        [System.Diagnostics.Process]$Proc,
        [string]$ToolName,
        [hashtable]$Arguments = @{}
    )
    $resolved = Resolve-ToolName -Canonical $ToolName
    $result = Send-JsonRpcRequest -Proc $Proc -Method 'tools/call' -Params @{
        name      = $resolved
        arguments = $Arguments
    }
    if ($result.content) {
        return ($result.content |
            Where-Object { $_.type -eq 'text' } |
            ForEach-Object { $_.text }) -join ''
    }
    return ''
}

# ── Pipeline Template Resolution ────────────────────────────────────────

function Resolve-DotPath {
    param($Obj, [string]$Path)
    $parts = $Path -split '\.'
    $current = $Obj
    foreach ($part in $parts) {
        if ($null -eq $current) { throw "Template path resolved to null before '$part'." }
        if ($part -match '^\d+$') {
            $idx = [int]$part
            if ($current -is [array] -or $current -is [System.Collections.IList]) {
                $current = $current[$idx]
            } else {
                $current = $current.$part
            }
        } else {
            $current = $current.$part
        }
    }
    if ($null -eq $current) { throw "Template path '$Path' resolved to null." }
    return $current
}

function Resolve-StepTemplates {
    param([hashtable]$Cmd, [array]$PrevResults)
    $resolved = @{}
    foreach ($key in @($Cmd.Keys)) {
        $val = $Cmd[$key]
        if ($val -is [string]) {
            while ($val -match '\{\{\$(\d+)\.([^}]+)\}\}') {
                $fullMatch = $Matches[0]
                $stepIdx = [int]$Matches[1]
                $dotPath = $Matches[2]
                if ($stepIdx -ge $PrevResults.Count) {
                    throw "Template '${fullMatch}': step $stepIdx hasn't run yet."
                }
                $ref = $PrevResults[$stepIdx]
                if (-not $ref.ok) {
                    throw "Template '${fullMatch}': step $stepIdx failed."
                }
                $replacement = [string](Resolve-DotPath -Obj $ref.result -Path $dotPath)
                $val = $val.Replace($fullMatch, $replacement)
            }
            $resolved[$key] = $val
        } else {
            $resolved[$key] = $val
        }
    }
    return $resolved
}

# ── Action Dispatch ─────────────────────────────────────────────────────

function Invoke-SingleAction {
    param(
        [System.Diagnostics.Process]$Proc,
        [hashtable]$Cmd
    )
    switch ($Cmd.action) {
        'list-tools' {
            $list = Send-JsonRpcRequest -Proc $Proc -Method 'tools/list' -Params @{}
            return ($list.tools | ForEach-Object {
                $desc = $_.description
                if ($desc.Length -gt 120) { $desc = $desc.Substring(0, 120) + '...' }
                [PSCustomObject]@{ Name = $_.name; Description = $desc }
            })
        }
        'repos' {
            $a = @{}
            if ($Cmd.repoPattern)  { $a.repoNamePattern = $Cmd.repoPattern }
            if ($Cmd.maxRepoCount) { $a.maxRepoCount = $Cmd.maxRepoCount }
            if ($Cmd.skip)         { $a.repoSkipCount = $Cmd.skip }
            return (Invoke-Tool -Proc $Proc -ToolName 'get_codebase_repos' -Arguments $a)
        }
        'branches' {
            if (-not $Cmd.repoName) { throw "branches: -RepoName is required." }
            $a = @{ repoName = $Cmd.repoName }
            if ($Cmd.branchPattern)  { $a.branchNamePattern = $Cmd.branchPattern }
            if ($Cmd.maxBranchCount) { $a.maxBranchCount = $Cmd.maxBranchCount }
            if ($Cmd.skip)           { $a.branchSkipCount = $Cmd.skip }
            return (Invoke-Tool -Proc $Proc -ToolName 'get_codebase_branches' -Arguments $a)
        }
        'search' {
            if (-not $Cmd.searchScope -or -not $Cmd.query) {
                throw "search: -SearchScope and -Query are required."
            }
            $a = @{ searchScope = $Cmd.searchScope; searchString = $Cmd.query }
            if ($Cmd.maxResults) { $a.maxResultCount = $Cmd.maxResults }
            if ($Cmd.maxHits)    { $a.maxHitCount = $Cmd.maxHits }
            if ($Cmd.skip)       { $a.resultSkipCount = $Cmd.skip }
            if ($Cmd.hitSkip)    { $a.hitSkipCount = $Cmd.hitSkip }
            return (Invoke-Tool -Proc $Proc -ToolName 'get_codebase_file_matches' -Arguments $a)
        }
        'content' {
            if (-not $Cmd.searchScope -or -not $Cmd.filePath) {
                throw "content: -SearchScope and -FilePath are required."
            }
            $a = @{
                searchScope = $Cmd.searchScope
                filePath    = $Cmd.filePath
                charOffset  = [int]$Cmd.charOffset
                length      = [int]$Cmd.length
            }
            if ($Cmd.revisionId) { $a.revisionId = $Cmd.revisionId }
            return (Invoke-Tool -Proc $Proc -ToolName 'get_codebase_file_text_range' -Arguments $a)
        }
        'content-all' {
            if (-not $Cmd.searchScope -or -not $Cmd.filePath) {
                throw "content-all: -SearchScope and -FilePath are required."
            }
            $pageSize = if ($Cmd.pageSize -and $Cmd.pageSize -gt 0) { [int]$Cmd.pageSize } else { 50000 }
            $maxTotal = if ($Cmd.maxTotalLength -and $Cmd.maxTotalLength -gt 0) { [int]$Cmd.maxTotalLength } else { 500000 }
            $offset = if ($Cmd.charOffset) { [int]$Cmd.charOffset } else { 0 }
            $allText = [System.Text.StringBuilder]::new()

            do {
                $a = @{
                    searchScope = $Cmd.searchScope
                    filePath    = $Cmd.filePath
                    charOffset  = $offset
                    length      = $pageSize
                }
                if ($Cmd.revisionId) { $a.revisionId = $Cmd.revisionId }
                $chunk = Invoke-Tool -Proc $Proc -ToolName 'get_codebase_file_text_range' -Arguments $a

                $chunkText = $chunk.text
                $actualLen = [int]$chunk.actualLength
                if ($actualLen -eq 0 -or -not $chunkText) { break }

                $null = $allText.Append($chunkText)
                $offset += $actualLen

                $reachedEnd = ($actualLen -lt $pageSize)
                $reachedLimit = ($allText.Length -ge $maxTotal)
            } while (-not $reachedEnd -and -not $reachedLimit)

            return [PSCustomObject]@{
                filePath    = $Cmd.filePath
                totalLength = $allText.Length
                truncated   = ($allText.Length -ge $maxTotal)
                text        = $allText.ToString()
            }
        }
        'history' {
            if (-not $Cmd.searchScope -or -not $Cmd.filePath) {
                throw "history: -SearchScope and -FilePath are required."
            }
            $a = @{
                searchScope            = $Cmd.searchScope
                filePath               = $Cmd.filePath
                includeStitchedHistory = (-not $Cmd.noStitchedHistory)
            }
            if ($Cmd.maxHistoryCount) { $a.maxResultCount = $Cmd.maxHistoryCount }
            if ($Cmd.skip)            { $a.resultSkipCount = $Cmd.skip }
            return (Invoke-Tool -Proc $Proc -ToolName 'get_codebase_file_history' -Arguments $a)
        }
        default { throw "Unknown action: $($Cmd.action)" }
    }
}

# ── Main ────────────────────────────────────────────────────────────────

$server = $null
try {
    $exePath = Resolve-ExePath -Explicit $McpExePath
    $server  = Start-McpServer -ExePath $exePath
    Initialize-Session -Proc $server

    # Discover and cache tool names
    $script:toolNameCache = Get-ToolMap -Proc $server

    if ($PipelineJson) {
        # ── Pipeline Mode ──
        $steps = $PipelineJson | ConvertFrom-Json
        $results = @()
        $stepIndex = 0
        foreach ($step in $steps) {
            $cmd = @{}
            $step.PSObject.Properties | ForEach-Object { $cmd[$_.Name] = $_.Value }
            try {
                $cmd = Resolve-StepTemplates -Cmd $cmd -PrevResults $results
                $r = Invoke-SingleAction -Proc $server -Cmd $cmd
                $results += [PSCustomObject]@{
                    step   = $stepIndex
                    action = $cmd.action
                    ok     = $true
                    result = $r
                }
            } catch {
                $results += [PSCustomObject]@{
                    step   = $stepIndex
                    action = $cmd.action
                    ok     = $false
                    error  = $_.Exception.Message
                }
            }
            $stepIndex++
        }
        $results | ConvertTo-Json -Depth 30
    }
    else {
        # ── Single Action Mode ──
        $cmd = @{
            action          = $Action
            repoPattern     = $RepoPattern
            repoName        = $RepoName
            branchPattern   = $BranchPattern
            searchScope     = $SearchScope
            query           = $Query
            filePath        = $FilePath
            charOffset      = $CharOffset
            length          = $Length
            revisionId      = $RevisionId
            maxResults      = $MaxResults
            maxHits         = $MaxHits
            skip            = $Skip
            hitSkip         = $HitSkip
            maxHistoryCount = $MaxHistoryCount
            maxTotalLength  = $MaxTotalLength
            noStitchedHistory = $NoStitchedHistory.IsPresent
        }
        $result = Invoke-SingleAction -Proc $server -Cmd $cmd
        if ($result -is [string]) {
            $result
        } else {
            $result | ConvertTo-Json -Depth 30
        }
    }
}
catch {
    [PSCustomObject]@{ ok = $false; error = $_.Exception.Message } |
        ConvertTo-Json -Depth 5
    exit 1
}
finally {
    if ($server) { Stop-McpServer -Proc $server }
}
