# Public Lucky Engine Plugin SDK preview

`LuckyEngine.PluginSdk.0.1.1-preview.1.nupkg` is the MIT-licensed public C# API
package from Lucky Engine's `PluginSDK/` project. It contains the public protocol
contract, native-widget descriptions and process client. It contains no private
engine assemblies, headers, application state or robot model assets.

This preview is pinned here so users and CI can build the plugin without an
engine checkout or an unpublished download URL. `NuGet.Config` registers this
relative package source alongside nuget.org. Runtime installations include the
compiled SDK dependency and do not run NuGet.

Source: <https://github.com/luckyrobots/LuckyEngine/pull/1099>, native-widget
styling change `8180b1b45`; public SDK package version `0.1.1-preview.1`.

SHA-256: `037E3A92F303BBE517091EFCAE5AA8137B2DC44106D997D8E56F1F1290027361`.

Optional `Padding`, `Spacing` and `Appearance` fields use native editor themes.
The API 1 protocol remains additive; older hosts ignore these style fields.
Metric robot calibration requires the matching host's robot geometry APIs.
