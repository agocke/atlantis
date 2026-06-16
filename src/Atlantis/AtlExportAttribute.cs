namespace Atlantis;

/// <summary>
/// Marks a static method for export to JavaScript through the Atlantis bridge.
/// 'atl bindgen' scans for these methods and generates the typed bindings that let
/// the webview call them as <c>atlantis.&lt;Class&gt;.&lt;Method&gt;(...)</c>. The host
/// must register a matching handler (see <see cref="Bridge.BridgeHost"/>); the
/// attribute itself only describes the contract used to generate the frontend.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AtlExportAttribute : Attribute;
