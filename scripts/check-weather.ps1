$ErrorActionPreference = 'Stop'
$cfg = Get-Content "$env:LOCALAPPDATA\HenryMonitor\config.json" -Raw | ConvertFrom-Json
$token = $cfg.accessToken
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$nonce = -join ((97..122) | Get-Random -Count 20 | ForEach-Object { [char]$_ })
$lf = [char]10
$canonical = "GET" + $lf + "/api/status" + $lf + $ts + $lf + $nonce + $lf
$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [Convert]::FromBase64String($token)
$sig = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))
$port = $cfg.port
try {
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/status" -Headers @{
        'X-HM-Timestamp' = "$ts"
        'X-HM-Nonce' = $nonce
        'X-HM-Signature' = $sig
    } -TimeoutSec 10
    Write-Output ("sequence=" + $r.sequence)
    if ($r.weather) {
        Write-Output ("WEATHER: " + $r.weather.temperatureC + "C, " + $r.weather.description + ", city=" + $r.weather.city + ", humidity=" + $r.weather.humidityPercent + "%, wind=" + $r.weather.windKmh + "km/h, isDay=" + $r.weather.isDay)
    } else {
        Write-Output "WEATHER: null (still fetching or unavailable)"
    }
} catch {
    Write-Output ("request failed: " + $_.Exception.Message)
}
