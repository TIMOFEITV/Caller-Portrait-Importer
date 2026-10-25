param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameDir).Path
$managedCandidates = Get-ChildItem -LiteralPath $game -Directory -Filter '*_Data' |
    ForEach-Object { Join-Path $_.FullName 'Managed' } |
    Where-Object { Test-Path (Join-Path $_ 'UnityEngine.CoreModule.dll') }
if (@($managedCandidates).Count -ne 1) {
    throw "Could not identify exactly one game Managed folder. Found $(@($managedCandidates).Count)."
}
$managed = @($managedCandidates)[0]
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET SDK to compile this mod.' }
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Plugin.cs') -Raw
if ($source.Contains('REPLACE_WITH_YOUR_TWITCH_PUBLIC_CLIENT_ID')) {
    Write-Warning 'Twitch Client ID is still a placeholder. Sign-in will be disabled until you insert your public Twitch application Client ID and rebuild.'
}
dotnet build "$PSScriptRoot\ScamWYF.TwitchControl.csproj" -c Release "-p:GameDir=$game" "-p:ManagedDir=$managed"
if ($LASTEXITCODE -ne 0) { throw 'Build failed; send the FIRST compiler error.' }
Write-Host "Build OK: $PSScriptRoot\bin\Release\net472\ScamWYF.TwitchControl.dll" -ForegroundColor Green
