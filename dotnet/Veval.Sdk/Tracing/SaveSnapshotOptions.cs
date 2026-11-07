namespace Veval.Sdk;

/// <summary>How <see cref="IVevalSdk.SaveSnapshotAsync(string, string, SaveSnapshotOptions?)"/> stores a baseline.</summary>
public class SaveSnapshotOptions
{
    /// <summary>
    /// Keep the baseline past the org's retention. On by default: a baseline you named and saved is meant
    /// to be kept, and an unpinned one disappears after the retention window, failing every comparison
    /// against its name. Pins are limited per plan; saving a new name at the limit throws a
    /// <see cref="VevalRequestException"/> with status 409. Saving again under a pinned name reuses its pin.
    /// Set to false to save a baseline that expires with retention.
    /// </summary>
    public bool Pinned { get; set; } = true;
}
