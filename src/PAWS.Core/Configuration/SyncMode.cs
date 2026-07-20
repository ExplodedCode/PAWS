namespace PAWS.Core.Configuration;

/// <summary>How a sync pair materializes cloud files on the local disk.</summary>
public enum SyncMode
{
    /// <summary>
    /// OneDrive-style: cloud files appear as on-demand placeholders and are hydrated
    /// (downloaded) only when opened. This is PAWS's headline mode.
    /// </summary>
    OnDemand,

    /// <summary>Every file is kept fully local and in the cloud (classic Dropbox/rclone behavior).</summary>
    FullSync,

    // TODO: Cloud-only mode (files live only in the cloud; nothing is downloaded unless explicitly
    // pinned) is not implemented — it was never more than this placeholder enum value, with the UI
    // silently treating it like FullSync's manual-only path. Re-add here (as the next value, 2, to keep
    // OnDemand=0/FullSync=1 stable) once it has a real engine: no eager placeholder hydration, an
    // explicit "keep on this device"/pin flow, and its own auto-sync semantics (push-only, no pull-to-
    // placeholder since there's nothing to place). Disabled 2026-07-02 rather than removed, since the
    // enum value, the "Cloud-only" mode-picker option, and the UI's default-case label all pointed at
    // this half-finished feature — see [[paws-architecture]] for the removal/re-add rationale.
    // CloudOnly,
}
