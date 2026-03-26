namespace Atlantis.Analyzers;

/// <summary>
/// Diagnostic IDs for Atlantis analyzers.
/// </summary>
public static class DiagnosticIds
{
    /// <summary>
    /// [JSExport] is deprecated. Use [AtlExport] instead.
    /// </summary>
    public const string JSExportObsolete = "ATL001";

    /// <summary>
    /// [AtlExport] requires the AtlExportAttribute class to be defined.
    /// </summary>
    public const string MissingAtlExportAttribute = "ATL002";

    /// <summary>
    /// System.Runtime.InteropServices.JavaScript using is not needed for Atlantis.
    /// </summary>
    public const string UnnecessaryJSInteropUsing = "ATL003";
}
