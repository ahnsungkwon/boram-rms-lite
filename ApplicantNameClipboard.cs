using System.Runtime.InteropServices;

namespace BoramRms.Lite;

internal enum ApplicantNameCopyResult { Copied, Unchanged, NoName, Superseded, Unavailable }

// Called on the UI thread. Each retry returns to that thread before writing, so a
// newer selection cannot be overtaken by a delayed clipboard operation.
internal sealed class ApplicantNameClipboard(Action<string> write, Func<TimeSpan, Task>? delay = null)
{
    private readonly Func<TimeSpan, Task> _delay = delay ?? (duration => Task.Delay(duration));
    private string? _path, _name;
    private long _version;
    private bool _copied;
    private Task<ApplicantNameCopyResult>? _pending;

    internal Task<ApplicantNameCopyResult> RequestAsync(string path, string name, Func<bool> isCurrent, bool force = false)
    {
        name = name.Trim();
        if (!force && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _name == name)
        {
            if (_pending != null) return _pending;
            if (_copied) return Task.FromResult(ApplicantNameCopyResult.Unchanged);
        }
        _path = path; _name = name; _copied = false;
        var version = ++_version;
        _pending = CopyAsync(name, version, isCurrent);
        return _pending;
    }

    internal void CancelPending(bool forgetSelection = false)
    {
        _version++; _pending = null;
        if (forgetSelection) { _path = _name = null; _copied = false; }
    }

    private async Task<ApplicantNameCopyResult> CopyAsync(string name, long version, Func<bool> isCurrent)
    {
        // Do not block image selection, and coalesce selections made in one UI turn.
        await Task.Yield();
        try
        {
            if (name.Length == 0) return ApplicantNameCopyResult.NoName;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (version != _version || !isCurrent()) return ApplicantNameCopyResult.Superseded;
                try
                {
                    write(name);
                    _copied = true;
                    return ApplicantNameCopyResult.Copied;
                }
                catch (ExternalException)
                {
                    if (attempt == 2) return ApplicantNameCopyResult.Unavailable;
                    await _delay(TimeSpan.FromMilliseconds(60 * (attempt + 1)));
                }
            }
            return ApplicantNameCopyResult.Unavailable;
        }
        finally { if (version == _version) _pending = null; }
    }
}
