$target = Get-Content -LiteralPath (Join-Path $root 'target.json') -Raw | ConvertFrom-Json
function CoreDialog {
    $byProcess = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byProcess)) {
        if ($window.Current.AutomationId -eq 'CoreConnectionWindow') { return $window }
        if ($nested = ById $window 'CoreConnectionWindow') { return $nested }
    }
}
function EnterInDialog($id, $text) {
    $field = Wait-Ui $id { ById $dialog $id }
    $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}
$testFolder = [IO.Directory]::CreateDirectory((Join-Path $root 'old-core-folder')).FullName
$null = & $cli --state $state bind --project $ProjectId --path $testFolder
if ($LASTEXITCODE) { throw 'Failed to prepare original folder binding' }
Click $main 'ManageCoreConnection'
$dialog = Wait-Ui 'Core address dialog' { CoreDialog }
$null = Wait-Ui 'Old inventory contains the local folder' {
    $line = ById $dialog 'CurrentPairingInventory'
    if ($line -and $line.Current.Name -match '1 个目录绑定|1 folder binding') { $line }
}
if ($ScreenshotDirectory) { Save-WindowsTestWindow $dialog $ScreenshotDirectory 'change-core-zh' }
Click $dialog 'CancelCoreChange'
$null = Wait-Ui 'Cancel keeps original project' { HasText $main 'ProjectHub' }
Click $main 'ManageCoreConnection'
$dialog = Wait-Ui 'Reopen Core address dialog' { CoreDialog }
EnterInDialog 'NewCoreAddress' $target.address
EnterInDialog 'NewCorePairingCode' ([guid]::NewGuid().ToString() + '.' + ('0' * 32))
Click $dialog 'ConfirmCoreChange'
$null = Wait-Ui 'Rejected pairing stays open' {
    $errorLabel = ById $dialog 'CoreChangeStatus'
    if ($errorLabel -and $errorLabel.Current.Name -match '未接受|did not accept') { return $errorLabel }
}
if ((& $cli --state $state status | ConvertFrom-Json).coreUrl -ne $status.coreUrl) { throw 'Rejected pairing changed local Core' }
Pass 'Cancel and rejected code preserve the old Core'
EnterInDialog 'NewCorePairingCode' $target.pairing
Click $dialog 'ConfirmCoreChange'
$null = Wait-Ui 'New project displayed' { HasText $main 'Second Core Project' }
$current = & $cli --state $state status | ConvertFrom-Json
if (([uri]$current.coreUrl).Port -ne ([uri]$target.address).Port) { throw 'GUI and CLI disagree on the active Core' }
if ((ById $main 'MachineName').Current.Name -eq '') { throw 'Missing new identity' }
Click $main 'ManageCoreConnection'
$dialog = Wait-Ui 'Read current address' { CoreDialog }
$null = Wait-Ui 'New Core address shown' { HasText $dialog $target.address }
$null = Wait-Ui 'Target pairing starts with an empty inventory' {
    $line = ById $dialog 'CurrentPairingInventory'
    if ($line -and $line.Current.Name -match '0 个目录绑定，0 条未发送|0 folder bindings?, 0 unsent') { $line }
}
Click $dialog 'CancelCoreChange'
Pass 'Successful switch shows grants from the target Core and the CLI uses the same identity'
