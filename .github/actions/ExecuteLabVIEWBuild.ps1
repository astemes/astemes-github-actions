param (
    [string]$ProjectPath,
    [string]$BuildSpec,
    [string]$Target = "My Computer",
    [string]$LogPath = "logs",
    [int]$MaxTries = 1
)

$buildScript = "@LabVIEWCLI -OperationName ExecuteBuildSpec -LabVIEWPath '$env:LV_PATH' -ProjectPath '$env:GITHUB_WORKSPACE\$ProjectPath' -TargetName '$Target' -BuildSpecName '$BuildSpec' -PortNumber $env:LV_PORT -LogFilePath '$env:GITHUB_WORKSPACE\$LogPath\LabVIEWCLI_ExecuteBuildSpec.txt' -LogToConsole true -Verbosity Minimal"
Write-Host $buildScript

$count = 0
while ($true) {
    try {
        $rawOut = cmd /c $buildScript
        break
    } catch {
        $count++
        Write-Host "ExecuteBuildSpec failed $count/$MaxTries"
        try {
            taskkill /IM labview.exe /F
        } catch {
            Write-Host "Could not kill LabVIEW: $_"
        }
        if ($count -eq $MaxTries) { throw }
    }
}

$index = $rawOut.IndexOf('Operation output:')
$buildOut = $rawOut.Substring($index)
$buildOutRows = $buildOut -split "`n"
$fileName = $buildOutRows[2].Trim()

Write-Host "Built LabVIEW Build Spec. Output: $fileName"
return $fileName