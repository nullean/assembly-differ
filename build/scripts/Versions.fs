module Versions

open Proc.Fs

/// dotnet/sdk#53783: on a cold tool-resolver cache (every fresh CI runner, every fresh container),
/// restoring 2+ RID-specific tool packages in one manifest can misattribute one package's
/// DotnetToolSettings.xml to another, failing with "The command ... is not contained in the
/// package ...". The cache is warm after the first attempt, so a bare retry always succeeds -
/// see https://github.com/dotnet/sdk/issues/53783.
let private restoreTools =
    lazy (shell {
        let! code = exitCode $"dotnet tool restore"
        if code <> 0 then sh $"dotnet tool restore"
    })

/// The version MinVer computes from the tags, which is the version the packages get
let current =
    lazy (shell {
        restoreTools.Value
        let! version = text $"dotnet minver -p canary.0 -m 0.1"
        return version
    })

/// The informational version the package validator expects: the version, plus the commit it was built from
let informational =
    lazy (shell {
        let! sha = text $"git rev-parse HEAD"
        return $"{current.Value}+{sha}"
    })
