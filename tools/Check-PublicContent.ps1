param([string]$ArtifactDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskFiles = @(git -C $taskRoot ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot list tracked files' }
$taskForbidden = '(?i)(^|/)(verification|release|artifacts|bin|obj)/|(^|/)(preferences|libraries)\.json$|\.(log|db|json\.gz|pdb)$|(^|/)\.env'
$taskPatterns = @('(?i)[A-Z]:[\\/]+Users[\\/]+[^\\/]+[\\/]', 'gh[pousr]_[A-Za-z0-9]{24,}', 'github_pat_[A-Za-z0-9_]{20,}', 'sk-[A-Za-z0-9]{24,}')
foreach ($taskFile in $taskFiles) {
    if ($taskFile -match $taskForbidden) { throw "Private/generated file is tracked: $taskFile" }
    if ($taskFile -match '\.json$' -and $taskFile -notmatch '^Resources/prob_(emit|trans)\.json$') { throw "Unapproved JSON data file: $taskFile" }
    if ($taskFile -match '\.ico$') { continue }
    $taskContent = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    foreach ($taskPattern in $taskPatterns) { if ($taskContent -match $taskPattern) { throw "Potential private content in $taskFile" } }
}
foreach ($taskCommit in @(git -C $taskRoot rev-list --all)) {
    foreach ($taskFile in @(git -C $taskRoot ls-tree -r --name-only $taskCommit)) {
        if ($taskFile -match $taskForbidden) { throw "Private file in history: $taskFile" }
        if ($taskFile -match '\.ico$') { continue }
        $taskContent = (git -C $taskRoot show "${taskCommit}:$taskFile") -join "`n"
        foreach ($taskPattern in $taskPatterns) { if ($taskContent -match $taskPattern) { throw "Potential private content in history: $taskFile" } }
    }
}
if ($ArtifactDirectory) {
    $taskAllowed = @('.exe', '.dll', '.json', '.txt', '.md')
    foreach ($taskFile in Get-ChildItem -LiteralPath $ArtifactDirectory -File -Recurse) {
        if ($taskAllowed -notcontains $taskFile.Extension -and $taskFile.Name -ne 'LICENSE') { throw "Unexpected artifact: $($taskFile.Name)" }
        if ($taskFile.Name -match '(?i)preferences|libraries|runtime\.log|\.db$|\.pdb$|index-') { throw "Private artifact: $($taskFile.Name)" }
        $taskBytes = [IO.File]::ReadAllBytes($taskFile.FullName)
        foreach ($taskEncoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
            $taskContent = $taskEncoding.GetString($taskBytes)
            foreach ($taskPattern in $taskPatterns) { if ($taskContent -match $taskPattern) { throw "Potential private content in artifact: $($taskFile.Name)" } }
        }
    }
}
Write-Output 'Public content check passed (tracked files, history, optional artifact).'
