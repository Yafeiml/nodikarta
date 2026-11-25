#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][guid]$GuestUuid,
    [Parameter(Mandatory)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
$ErrorView = 'NormalView'
trap { Write-Host $_.ScriptStackTrace; break }
function Check($condition, [string]$message) { if (!$condition) { throw $message } }
Check ($GuestUuid -ne [guid]::Empty -and [guid](Get-CimInstance Win32_ComputerSystemProduct).UUID -eq $GuestUuid -and
    (Get-CimInstance Win32_ComputerSystem).Model -match 'VMware|Virtual Machine|VirtualBox|KVM|QEMU') 'Use the designated disposable Windows VM.'
$registry = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/Nodicarta.Edge'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nodicarta Edge.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Nodicarta Edge.lnk'
foreach ($path in $registry,$menu,$startup,($menu -replace 'Nodicarta Edge','ProjectHub Edge'),($startup -replace 'Nodicarta Edge','ProjectHub Edge')) {
    Check (!(Test-Path -LiteralPath $path)) 'Restore the clean VM snapshot.'
}
Check (!(Get-Process -Name ProjectHub.Edge.Desktop,ProjectHub.Edge -ErrorAction Ignore)) 'The VM already has a running Edge.'

Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$Baseline = (Get-Item -LiteralPath $Baseline).FullName
$Candidate = (Get-Item -LiteralPath $Candidate).FullName
$old = [Diagnostics.FileVersionInfo]::GetVersionInfo($Baseline)
$new = [Diagnostics.FileVersionInfo]::GetVersionInfo($Candidate)
Check ($old.Comments -eq 'Nodicarta Edge public installer' -and $new.Comments -ceq $old.Comments -and
    [version]$new.ProductVersion -gt [version]$old.ProductVersion) 'Supply two actual public installers in ascending order.'
$work = [IO.Directory]::CreateTempSubdirectory('nodicarta-setup-mcp-')
$install = Join-Path $work.FullName 'Edge install'
$copy = Join-Path $work.FullName 'Independent Edge'
$state = Join-Path $work.FullName 'Isolated device state'
$env:NODICARTA_EDGE_STATE = $state
$manifest = Join-Path $install 'SHA256SUMS'
$launcher = Join-Path $install 'ProjectHub.Edge.exe'
$desktop = Join-Path $install 'ProjectHub.Edge.Desktop.exe'
$journal = Join-Path $install '.nodicarta-install-transaction.json'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()

function Start-TestProcess([string]$file, [string[]]$arguments, [switch]$Redirect, [switch]$Setup) {
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $work.FullName
    $info.CreateNoWindow = $Redirect.IsPresent
    $info.RedirectStandardInput = $info.RedirectStandardOutput = $info.RedirectStandardError = $Redirect.IsPresent
    if ($Setup) { $info.Arguments = $arguments -join ' ' }  # NSIS requires its unquoted /D= argument last.
    else { foreach ($arg in $arguments) { $info.ArgumentList.Add($arg) } }
    $p = [Diagnostics.Process]::Start($info)
    $owned.Add($p)
    $p
}
function Wait-For([string]$label, [scriptblock]$find) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $item = & $find
        if ($item) { return $item }
        Start-Sleep -Milliseconds 100
    } until ([DateTime]::UtcNow -ge $deadline)
    throw "Timed out waiting for $label"
}
function Assert-Exit([Diagnostics.Process]$process, [int]$code) {
    Check ($process.WaitForExit(90000)) 'Timed out waiting for setup to exit.'
    Check ($process.ExitCode -eq $code) "Expected setup exit $code, got $($process.ExitCode)."
}
function Find-Window([int]$pidValue, [string[]]$titles) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$pidValue)
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition) |
        Where-Object { !$titles -or $_.Current.Name -in $titles } | Select-Object -First 1
}
function Find-Button($window, [string]$id, [string]$label) {
    if (!$window) { return }
    $condition = if ($id) { [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id) }
        else { [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button) }
    $window.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.IsEnabled -and
        ($_.Current.Name.Replace('&','') -match $label)
    } | Select-Object -First 1
}
function Invoke-Button($element) {
    Check $element 'The expected installer button was unavailable.'
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Start-Interactive {
    $p = Start-TestProcess $Candidate @("/D=$install") -Setup
    $button = Wait-For 'the test setup Install button' { Find-Button (Find-Window $p.Id) '1' 'Install|安装|安 装' }
    Invoke-Button $button
    $p
}
function Find-SetupWorker([Diagnostics.Process]$p) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id) AND Name='ProjectHub.Edge.Desktop.exe'" |
        Where-Object { $_.ExecutablePath -like "$([IO.Path]::GetTempPath())ns*\bin\ProjectHub.Edge.Desktop.exe" } |
        Select-Object -First 1
}
function Ask-ForDisconnect([Diagnostics.Process]$p, [bool]$accept) {
    $worker = Wait-For 'the private installer worker' { Find-SetupWorker $p }
    $owned.Add([Diagnostics.Process]::GetProcessById($worker.ProcessId))
    $window = Wait-For 'the MCP disconnect confirmation' { Find-Window $worker.ProcessId @('Disconnect MCP and update','断开 MCP 并更新') }
    $decline = Find-Button $window '7' 'No|否'
    $approve = Find-Button $window '6' 'Yes|是'
    Check ($decline -and $approve -and $decline.Current.HasKeyboardFocus) 'The shutdown prompt must offer both choices and default to No.'
    if ($accept) { Invoke-Button $approve } else { Invoke-Button $decline }
}
function Close-Setup([Diagnostics.Process]$p, [int]$code) {
    $null = Wait-For 'setup completion' {
        if ($p.HasExited) { return $true }
        $window = Find-Window $p.Id
        $close = if ($code -eq 0) { Find-Button $window '1' 'Close|关闭|关 闭|Finish|完成' }
            else { Find-Button $window '2' 'Cancel|取消|取 消' }
        if ($close) { Invoke-Button $close; return $true }
    }
    Assert-Exit $p $code
}
function Query-Mcp($process, [int]$id, [string]$method, $parameters) {
    $request = @{jsonrpc='2.0'; id=$id; method=$method; params=$parameters} | ConvertTo-Json -Depth 6 -Compress
    $process.StandardInput.WriteLine($request)
    $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(12)).GetAwaiter().GetResult()
    $response = $line | ConvertFrom-Json
    Check ($response.id -eq $id -and $null -ne $response.result) "Invalid MCP reply for $method."
    $response.result
}
function Start-Mcp([string]$path, [string]$data, [string]$version, [switch]$StateFirst) {
    $argsForMcp = if ($StateFirst) { @('--state',$data,'mcp-stdio-global') } else { @('mcp-stdio-global','--state',$data) }
    $mcp = Start-TestProcess $path $argsForMcp -Redirect
    $connected = Query-Mcp $mcp 1 'initialize' @{protocolVersion='2025-11-25';clientInfo=@{name='setup-mcp-qa';version='1'};capabilities=@{}}
    Check ($connected.serverInfo.version -ceq $version) 'Unexpected MCP release version.'
    $mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $mcp
}
function Check-Session($process) {
    Check ((Query-Mcp $process 2 'tools/list' @{}).tools.name -contains 'get_edge_status') 'An MCP session was unexpectedly interrupted.'
}
function Check-Unchanged {
    Check ((Get-FileHash -LiteralPath $manifest).Hash -ceq $before -and !(Test-Path -LiteralPath $journal) -and
        !(Test-Path -LiteralPath (Join-Path $install '.nodicarta-install-backup'))) 'A declined or deferred update modified the installation.'
    Check-Session $session1
    Check-Session $session2
    Check-Session $independent
}

try {
    Write-Host 'Installing the baseline in the disposable guest.'
    Assert-Exit (Start-TestProcess $Baseline @('/S',"/D=$install") -Setup) 0
    $previous = Wait-For 'the old desktop' { Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction Ignore | Where-Object Path -eq $desktop }
    Copy-Item -LiteralPath $install -Destination $copy -Recurse
    $sentinel = Join-Path $state 'preserve-during-update.txt'
    [IO.File]::WriteAllText($sentinel,'isolated state')
    $shell = New-Object -ComObject WScript.Shell
    $menuArgs = $shell.CreateShortcut($menu).Arguments
    $startupPresent = Test-Path -LiteralPath $startup
    $before = (Get-FileHash -LiteralPath $manifest).Hash
    $session1 = Start-Mcp $launcher $state $old.ProductVersion
    $session2 = Start-Mcp $launcher $state $old.ProductVersion -StateFirst
    $independent = Start-Mcp (Join-Path $copy 'ProjectHub.Edge.exe') (Join-Path $work.FullName 'Other state') $old.ProductVersion

    Write-Host 'Checking silent and automatic deferral.'
    Assert-Exit (Start-TestProcess $Candidate @('/S',"/D=$install") -Setup) 1
    Assert-Exit (Start-TestProcess $Candidate @('/S','/UPDATE',"/D=$install") -Setup) 75
    Check-Unchanged

    Write-Host 'Declining the interactive disconnect.'
    $cancel = Start-Interactive
    Ask-ForDisconnect $cancel $false
    Assert-Exit $cancel 1602
    Check-Unchanged

    Write-Host 'Checking ordinary CLI protection.'
    $commandState = Join-Path $work.FullName 'ordinary mcp-stdio-global command'
    $ordinary = Start-TestProcess $launcher @('--state',$commandState,'pair','--core','http://127.0.0.1:1','--label','mcp-stdio') -Redirect
    $null = Wait-For 'the ordinary command holding a lease' { Test-Path -LiteralPath (Join-Path $commandState 'edge.sqlite3') }
    $blocked = Start-Interactive
    $worker = Wait-For 'the worker blocked by a local command' { Find-SetupWorker $blocked }
    $owned.Add([Diagnostics.Process]::GetProcessById($worker.ProcessId))
    $message = Wait-For 'the ordinary-command error dialog' {
        Check (!(Find-Window $worker.ProcessId @('Disconnect MCP and update','断开 MCP 并更新'))) 'A CLI was misclassified as an MCP process.'
        Find-Window $worker.ProcessId @('Nodicarta Edge')
    }
    $ok = Find-Button $message '' 'OK|O K|确定|确 定|关闭|关 闭'
    if ($ok) { Invoke-Button $ok }
    else { $message.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
    Close-Setup $blocked 1
    Check (!$ordinary.HasExited) 'An ordinary Edge command was terminated.'
    Check-Unchanged
    $ordinary.StandardInput.Close()
    Check ($ordinary.WaitForExit(15000)) 'The test CLI failed to close.'

    Write-Host 'Approving the disconnect and checking the fixed launcher.'
    $approved = Start-Interactive
    Ask-ForDisconnect $approved $true
    Close-Setup $approved 0
    Check ($session1.WaitForExit(10000) -and $session2.WaitForExit(10000) -and $previous.HasExited -and
        (Get-FileHash -LiteralPath $manifest).Hash -cne $before -and !(Test-Path -LiteralPath $journal)) 'The approved update did not complete in place.'
    Check-Session $independent
    $fresh = Start-Mcp $launcher $state $new.ProductVersion
    Check-Session $fresh
    $resident = Wait-For 'the updated desktop' { Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction Ignore | Where-Object Path -eq $desktop }
    Check ($resident.MainModule.FileVersionInfo.ProductVersion -ceq $new.ProductVersion -and
        [IO.File]::ReadAllText($sentinel) -ceq 'isolated state' -and
        $shell.CreateShortcut($menu).TargetPath -eq $launcher -and $shell.CreateShortcut($menu).Arguments -eq $menuArgs -and
        (Test-Path -LiteralPath $startup) -eq $startupPresent) 'The update lost the installed version, fixed path or user state.'
    [ordered]@{windows=[Environment]::OSVersion.Version.ToString(); baseline=$old.ProductVersion; candidate=$new.ProductVersion;
        installerSha256=(Get-FileHash -LiteralPath $Candidate).Hash; quietAndAutomaticPreservedMcp=$true;
        cancelledWithoutChanges=$true; ordinaryCliPreserved=$true; independentMcpPreserved=$true;
        installedAndReopenedMcpAtFixedPath=$true; stateAndShortcutsPreserved=$true} |
        ConvertTo-Json | Set-Content -LiteralPath $Evidence
    'PASS: quiet and automatic deferral, safe default cancel, ordinary CLI protection, confirmed MCP disconnect, fixed-path upgrade.'
} finally {
    foreach ($p in $owned) {
        try { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() } } finally { $p.Dispose() }
    }
    foreach ($p in @(Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction Ignore | Where-Object Path -eq $desktop)) {
        try { $p.Kill(); $p.WaitForExit() } finally { $p.Dispose() }
    }
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and
        $work.Name.StartsWith('nodicarta-setup-mcp-')) { Remove-Item -LiteralPath $work.FullName -Recurse -Force }
}
