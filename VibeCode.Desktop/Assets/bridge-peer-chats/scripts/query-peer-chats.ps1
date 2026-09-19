[CmdletBinding()]
param(
    [ValidateSet('list', 'search', 'read')][string] $Action = 'list',
    [string] $Agent,
    [string] $Query,
    [ValidateRange(0, 2147483647)][int] $Offset = 0,
    [ValidateRange(1, 50)][int] $Limit = 10,
    [ValidateRange(0, 2147483647)][int] $TextOffset = 0,
    [ValidateRange(100, 16000)][int] $MaxCharacters = 2000,
    [switch] $IncludeClosed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$archiveRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Read-ArchiveJson([string] $Name) {
    $path = Join-Path $archiveRoot $Name
    $file = Get-Item -LiteralPath $path
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Archive file is redirected.' }
    # Atomic replacement by VibeCode is allowed even while the reader holds the old complete snapshot open.
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
    finally { $reader.Dispose() }
}

function Read-Chat($Entry) {
    if ($Entry.chatId -notmatch '^[a-fA-F0-9]+$') { throw 'Invalid chat ID in archive.' }
    $document = Read-ArchiveJson ($Entry.chatId + '.json')
    if ($document.archiveId -cne $catalog.archiveId -or $document.chat.chatId -cne $Entry.chatId) {
        throw 'Chat does not belong to this bridge archive.'
    }
    if ($Agent -match '^\d{1,3}$' -and (!$document.chat.active -or $document.chat.agentNumber -ne [int] $Agent)) {
        throw 'The bridge roster changed during this lookup. List again and use the stable chat ID.'
    }
    return $document.chat
}

function Message-Excerpt($Chat, $Message, [int] $Start, [int] $Length) {
    $body = [string] $Message.text
    $startAt = [Math]::Min($Start, $body.Length)
    $lengthToRead = [Math]::Min($Length, $body.Length - $startAt)
    $nextText = $null
    if ($startAt + $lengthToRead -lt $body.Length) { $nextText = $startAt + $lengthToRead }
    return [ordered]@{
        chatId = $Chat.chatId; agentNumber = $Chat.agentNumber; provider = $Chat.provider
        active = $Chat.active; updatedAt = $Chat.updatedAt; index = $Message.index
        role = $Message.role; tool = $Message.tool; inProgress = $Message.inProgress
        text = $body.Substring($startAt, $lengthToRead); textOffset = $startAt
        textLength = $body.Length; nextTextOffset = $nextText
    }
}

try {
    $catalog = Read-ArchiveJson 'index.json'
    $chats = @($catalog.chats | Where-Object { $_.active -or $IncludeClosed })
    if ($Agent) {
        # Numeric identities are current roster positions. Stable IDs can retrieve closed peers explicitly.
        if ($Agent -match '^\d{1,3}$') {
            $chats = @($catalog.chats | Where-Object { $_.active -and $_.agentNumber -eq [int] $Agent })
        } else {
            $chats = @($catalog.chats | Where-Object { $_.chatId -ceq $Agent })
        }
        if ($chats.Count -eq 0) { throw 'Agent was not found in this bridge. List the roster for current IDs.' }
    }
    $chats = @($chats | Sort-Object @{ Expression = 'active'; Descending = $true }, agentNumber, chatId)
    $results = New-Object 'Collections.Generic.List[object]'
    $total = 0
    $nextOffset = $null
    # Bound the aggregate output as well as each excerpt; a large query should not fill the context window.
    $remainingCharacters = 32000
    switch ($Action) {
        'list' {
            $total = $chats.Count
            foreach ($entry in @($chats | Select-Object -Skip $Offset -First $Limit)) { $results.Add($entry) }
        }
        'read' {
            if (!$Agent -or $chats.Count -ne 1) { throw 'Read requires -Agent with one agent number or stable chat ID.' }
            $chat = Read-Chat $chats[0]
            $total = @($chat.messages).Count
            foreach ($message in @($chat.messages | Select-Object -Skip $Offset -First $Limit)) {
                if ($remainingCharacters -le 0) { break }
                $excerpt = Message-Excerpt $chat $message $TextOffset ([Math]::Min($MaxCharacters, $remainingCharacters))
                $remainingCharacters -= $excerpt.text.Length
                $results.Add($excerpt)
            }
        }
        'search' {
            if ([string]::IsNullOrWhiteSpace($Query)) { throw 'Search requires a non-empty -Query.' }
            foreach ($entry in $chats) {
                $chat = Read-Chat $entry
                foreach ($message in $chat.messages) {
                    $match = ([string] $message.text).IndexOf($Query, [StringComparison]::OrdinalIgnoreCase)
                    if ($match -lt 0) { continue }
                    $hit = $total++
                    if ($hit -lt $Offset -or $results.Count -ge $Limit -or $remainingCharacters -le 0) { continue }
                    $excerptLength = [Math]::Min($MaxCharacters, $remainingCharacters)
                    $prefixLength = [Math]::Min(120, [int] ($excerptLength / 4))
                    $excerpt = Message-Excerpt $chat $message ([Math]::Max(0, $match - $prefixLength)) $excerptLength
                    $remainingCharacters -= $excerpt.text.Length
                    $results.Add($excerpt)
                }
            }
        }
    }
    if ($Offset + $results.Count -lt $total) { $nextOffset = $Offset + $results.Count }
    [ordered]@{
        archiveId = $catalog.archiveId; action = $Action; updatedAt = $catalog.updatedAt
        total = $total; offset = $Offset; nextOffset = $nextOffset; results = @($results.ToArray())
    } | ConvertTo-Json -Depth 12 -Compress
} catch {
    [ordered]@{ error = $_.Exception.Message; action = $Action } | ConvertTo-Json -Compress
    exit 1
}
