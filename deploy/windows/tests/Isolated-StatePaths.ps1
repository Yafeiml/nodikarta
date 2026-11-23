function Get-EdgeKnownFolder($Folder) { if ($Folder -eq 'CommonApplicationData') { $testMachine } else { $testLocal } }
function Invoke-EdgeStateMigration($Cli,$Plan) {
    if (!$Plan.Legacy) { return }
    $dll = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -LiteralPath $Cli) 'ProjectHub.Edge.dll')))
    $type = $dll.GetType('ProjectHub.Edge.EdgeStatePaths',$true)
    $scope = $type.GetMethod('UseKnownFoldersForTest',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,[string[]]@($testLocal,$testMachine))
    try {
        $resolved = $type.GetMethod('Resolve',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,[string[]]@($Plan.Path))
        if ($resolved -ne $Plan.Path) { throw 'Unexpected state path' }
    } finally { $scope.Dispose() }
}
