# Fires the PDF's "concurrent events" sequence at the API at (almost) the same moment, then prints the final status.
#   truck arrives NORTH / emergency arrives EAST / admin asks for WEST / duplicate emergency / controller ACK
# Run with the app running. Use the http:// URL (no certificate problems).
#   powershell -File docs\concurrency-demo.ps1 -Base http://localhost:5000
param([string]$Base = "http://localhost:5000", [string]$Junction = "A")

$client = [System.Net.Http.HttpClient]::new()

function Start-Post([string]$path, $body) {
    $json = $body | ConvertTo-Json -Compress
    $content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
    return $client.PostAsync("$Base$path", $content)
}

$now = (Get-Date).ToUniversalTime().ToString("o")
$id = [guid]::NewGuid().ToString("N").Substring(0, 6)
$status = Invoke-RestMethod "$Base/api/junctions/$Junction/status"
$pendingId = $status.pending_command.command_id

$truck = @{ event_id = "conc-truck-$id"; junction_id = $Junction; direction = "NORTH"; event_type = "VEHICLE_ARRIVED"; vehicle_id = "TRK-$id"; vehicle_type = "TRUCK"; sequence_no = 7001; timestamp = $now }
$emergency = @{ event_id = "conc-emg-$id"; junction_id = $Junction; direction = "EAST"; event_type = "VEHICLE_ARRIVED"; vehicle_id = "AMB-$id"; vehicle_type = "EMERGENCY"; sequence_no = 7002; timestamp = $now }

$tasks = @()
$tasks += Start-Post "/api/sensor-events" $truck                                                          # T = 0 ms
$tasks += Start-Post "/api/sensor-events" $emergency                                                      # T = 4 ms
$tasks += Start-Post "/api/junctions/$Junction/commands" @{ command = "MANUAL_GREEN_REQUEST"; direction = "WEST" }   # T = 8 ms
$tasks += Start-Post "/api/sensor-events" $emergency                                                      # T = 12 ms (duplicate)
if ($pendingId) { $tasks += Start-Post "/api/controller-events" @{ command_id = $pendingId; junction_id = $Junction; status = "ACK" } }   # T = 17 ms

[System.Threading.Tasks.Task]::WaitAll($tasks)
foreach ($t in $tasks) { "{0}  {1}" -f [int]$t.Result.StatusCode, $t.Result.Content.ReadAsStringAsync().Result }

""
"Final status (desired signals must never contain two conflicting GREENs):"
Invoke-RestMethod "$Base/api/junctions/$Junction/status" | ConvertTo-Json -Depth 6
