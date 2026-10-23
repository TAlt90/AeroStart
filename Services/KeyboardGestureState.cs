namespace AeroStart.Services;

public enum KeyboardGesture { None, HotkeyReleased, WindowsReleased }

public sealed class KeyboardGestureState
{
    private readonly bool[] _down = new bool[256];
    private bool _hotkeyArmed;
    private int _windowsCandidate;

    public void Reset(Func<int, bool>? isDown = null)
    {
        _hotkeyArmed = false;
        _windowsCandidate = 0;
        for (var key = 0; key < _down.Length; key++) _down[key] = isDown?.Invoke(key) == true;
    }

    public void Cancel()
    {
        _hotkeyArmed = false;
        _windowsCandidate = 0;
    }

    public KeyboardGesture Process(int key, bool pressed, bool injected, bool experimental)
    {
        if (key is < 0 or >= 256) return KeyboardGesture.None;
        if (injected)
        {
            Cancel();
            return KeyboardGesture.None;
        }
        var wasDown = _down[key];
        var repeat = pressed && wasDown;
        _down[key] = pressed;
        if (repeat) return KeyboardGesture.None;
        if (pressed)
        {
            if (key == 0x20)
                _hotkeyArmed = (_down[0xA2] || _down[0xA3]) && (_down[0xA4] || _down[0xA5]) && !OtherKeysDown(hotkey: true);
            else _hotkeyArmed = false;
            if (experimental && key is 0x5B or 0x5C)
            {
                var alone = !OtherKeysDown(hotkey: false, except: key);
                _windowsCandidate = alone ? key : 0;
            }
            else _windowsCandidate = 0;
        }
        else
        {
            if (key == 0x20)
            {
                var invoke = wasDown && _hotkeyArmed;
                _hotkeyArmed = false;
                if (invoke) return KeyboardGesture.HotkeyReleased;
            }
            if (key is 0x5B or 0x5C)
            {
                var invoke = experimental && wasDown && _windowsCandidate == key;
                _windowsCandidate = 0;
                if (invoke) return KeyboardGesture.WindowsReleased;
            }
        }
        return KeyboardGesture.None;
    }

    private bool OtherKeysDown(bool hotkey, int except = -1)
    {
        for (var key = 8; key < _down.Length; key++)
        {
            if (!_down[key] || key == except) continue;
            if (hotkey && key is 0x20 or 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5) continue;
            return true;
        }
        return false;
    }
}
