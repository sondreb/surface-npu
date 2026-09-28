using Windows.Graphics.Imaging;

namespace SurfaceAIStudio;

internal sealed class PhotoDocument
{
    const int UndoLimit = 8;

    readonly List<SoftwareBitmap> _undo = [];
    readonly List<SoftwareBitmap> _redo = [];

    public SoftwareBitmap? Current { get; private set; }

    public SoftwareBitmap? Original { get; private set; }

    public string Title { get; private set; } = "Untitled";

    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int Width => Current?.PixelWidth ?? 0;

    public int Height => Current?.PixelHeight ?? 0;

    public void Open(SoftwareBitmap bitmap, string title)
    {
        DisposeHistory();
        Original?.Dispose();
        Current?.Dispose();
        Current = bitmap;
        Original = SoftwareBitmap.Copy(bitmap);
        Title = title;
        IsDirty = false;
    }

    public void Replace(SoftwareBitmap next)
    {
        if (Current is not null)
        {
            _undo.Add(Current);
            while (_undo.Count > UndoLimit)
            {
                _undo[0].Dispose();
                _undo.RemoveAt(0);
            }
        }

        foreach (var item in _redo)
        {
            item.Dispose();
        }

        _redo.Clear();
        Current = next;
        IsDirty = true;
    }

    public bool Undo()
    {
        if (Current is null || _undo.Count == 0)
        {
            return false;
        }

        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IsDirty = true;
        return true;
    }

    public bool Redo()
    {
        if (Current is null || _redo.Count == 0)
        {
            return false;
        }

        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        IsDirty = true;
        return true;
    }

    public void NoteSaved(string title)
    {
        Title = title;
        IsDirty = false;
    }

    public void Dispose()
    {
        DisposeHistory();
        Current?.Dispose();
        Original?.Dispose();
        Current = null;
        Original = null;
    }

    void DisposeHistory()
    {
        foreach (var item in _undo)
        {
            item.Dispose();
        }

        foreach (var item in _redo)
        {
            item.Dispose();
        }

        _undo.Clear();
        _redo.Clear();
    }
}
