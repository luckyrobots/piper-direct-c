# Public Lucky Engine Plugin SDK preview

`LuckyEngine.PluginSdk.0.1.2-preview.1.nupkg` is the MIT-licensed public C# API
package from Lucky Engine's `PluginSDK/` project. It contains the public protocol
contract, native-widget descriptions and process client. It contains no private
engine assemblies, headers, application state or robot model assets.

This preview is pinned here so users and CI can build the plugin without an
engine checkout or an unpublished download URL. `NuGet.Config` registers this
relative package source alongside nuget.org. Runtime installations include the
compiled SDK dependency and do not run NuGet.

Source: packed locally with `dotnet pack PluginSDK/LuckyEngine.PluginSdk -c Release`
from the Lucky Engine branch `eugene/office-pipers-linux-build`
(<https://github.com/luckyrobots/LuckyEngine/pull/1128>), which adds plugin API 2;
public SDK package version `0.1.2-preview.1`. It is not a published release yet:
replace it with the released package, and its SHA-256, once that PR merges.

SHA-256: `4DAA368D3609837DA3890EA97ACA3275A237A8482162DF5AE630062944F67731`.

Plugin API 2 adds the `tabs` and `segmented` widgets, `Icon` on buttons, `Icons`
on tabs and the `nav` / `nav-selected` button appearances. An API 1 editor
refuses an `api: 2` package at install. Optional `Padding`, `Spacing` and
`Appearance` fields use native editor themes.
Metric robot calibration requires the matching host's robot geometry APIs.
