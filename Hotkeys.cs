using System.Runtime.InteropServices;

namespace Primicord;

/// <summary>
/// Atalho global: funciona com o jogo em primeiro plano, sem o Primicord ter foco.
/// </summary>
/// <remarks>
/// Usa RegisterHotKey (e NAO um hook de teclado tipo WH_KEYBOARD_LL) de proposito:
/// o hook enxergaria TUDO que o usuario digita — inclusive senhas e conversa — e num
/// app que tem chat isso e inaceitavel. O RegisterHotKey so avisa quando a combinacao
/// registrada e pressionada; o resto das teclas nunca passa por aqui.
///
/// O Windows recusa o registro se outro programa ja tomou a combinacao (o Discord, por
/// exemplo, costuma segurar algumas). Nesse caso Register devolve false e a UI avisa
/// pra escolher outra tecla, em vez de ficar quieto e o usuario achar que gravou.
/// </remarks>
public sealed class HotkeyBinding
{
    public const int WmHotkey = 0x0312;

    [Flags]
    public enum Mods
    {
        None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly int _id;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _registered;

    public Mods Modifiers { get; private set; }
    public Keys Key { get; private set; }

    public HotkeyBinding(int id) => _id = id;

    public bool IsRegistered => _registered;

    /// <summary>Registra a combinacao. false = alguem ja tem essa tecla.</summary>
    public bool Register(IntPtr hwnd, Mods mods, Keys key)
    {
        Unregister();
        if (key == Keys.None) return false;

        _hwnd = hwnd;
        Modifiers = mods;
        Key = key;
        _registered = RegisterHotKey(hwnd, _id, (uint)(mods | Mods.NoRepeat), (uint)key);
        if (!_registered)
            Log.Write($"atalho {Format(mods, key)} recusado pelo Windows (ja em uso?)");
        else
            Log.Write($"atalho registrado: {Format(mods, key)}");
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero) return;
        try { UnregisterHotKey(_hwnd, _id); } catch { }
        _registered = false;
    }

    public bool Matches(Message m) => m.Msg == WmHotkey && m.WParam.ToInt32() == _id;

    // ─── serializacao pro config.txt ─────────────────────────────────────────

    public static string Serialize(Mods mods, Keys key) => $"{(int)(mods & ~Mods.NoRepeat)}:{(int)key}";

    public static (Mods Mods, Keys Key) Parse(string s, Mods defMods, Keys defKey)
    {
        try
        {
            var parts = s.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int k) && k != 0)
                return ((Mods)m, (Keys)k);
        }
        catch { }
        return (defMods, defKey);
    }

    /// <summary>"CTRL + ALT + C" — pra mostrar na tela.</summary>
    public static string Format(Mods mods, Keys key)
    {
        if (key == Keys.None) return "(nenhuma)";
        var parts = new List<string>();
        if (mods.HasFlag(Mods.Control)) parts.Add("CTRL");
        if (mods.HasFlag(Mods.Alt)) parts.Add("ALT");
        if (mods.HasFlag(Mods.Shift)) parts.Add("SHIFT");
        if (mods.HasFlag(Mods.Win)) parts.Add("WIN");
        parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "NUM " + (k - Keys.NumPad0),
        Keys.Oemtilde => "'",
        Keys.OemQuestion => "?",
        Keys.OemPeriod => ".",
        Keys.Oemcomma => ",",
        _ => k.ToString().ToUpperInvariant(),
    };

    /// <summary>Modificadores no formato do RegisterHotKey a partir do estado atual.</summary>
    public static Mods ModsFrom(Keys modifierKeys)
    {
        var m = Mods.None;
        if ((modifierKeys & Keys.Control) != 0) m |= Mods.Control;
        if ((modifierKeys & Keys.Alt) != 0) m |= Mods.Alt;
        if ((modifierKeys & Keys.Shift) != 0) m |= Mods.Shift;
        return m;
    }

    /// <summary>true se a tecla sozinha nao serve como atalho (so modificador).</summary>
    public static bool IsModifierOnly(Keys k) => k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
        or Keys.LWin or Keys.RWin or Keys.None;
}
