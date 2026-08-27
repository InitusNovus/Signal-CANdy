[CmdletBinding()]
param(
    [ValidateSet('Ci', 'Replay', 'Minimize')]
    [string] $Mode = 'Ci',
    [string] $CaseId,
    [string] $Input,
    [switch] $KeepFailures
)

throw 'SCHARDENING_RED production hardening entrypoint is not implemented'
