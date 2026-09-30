@{
    RootModule        = 'Trenal.Tools.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '6f0f9c62-3a39-4a8c-9a8e-7d6c0b0f5a21'
    Author            = 'trenal'
    Description       = 'Unix command shims (curl, wget, grep, head, tail, wc, touch, which...) for hosts that cannot run native executables.'
    PowerShellVersion = '7.4'
    FunctionsToExport = @('curl', 'wget', 'grep', 'head', 'tail', 'wc', 'touch', 'which', 'env', 'export', 'whoami', 'uname', 'ssh', 'scp', 'ssh-keygen')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
