using System.Text;

namespace VibeCode.Terminal;

public sealed partial class TerminalConsole
{
    private sealed record ControlHit(int Index, int X, int Y, int Width);
    private readonly List<ControlHit> _controlHits = [];
    private int _controlFocus = -1, _openControl = -1, _menuSelection, _menuOffset, _hoverControl = -1;
    private int _menuX, _menuY, _menuWidth, _menuVisible;
    private bool _settingPending;
    private bool _compactControls;
    private uint _mouseButtons;
    private static readonly string[] ControlNames = ["model", "effort", "fast"];
    private int ControlCount => _snapshot.Attachments is not null ? 5 : _snapshot.Speech is not null ? 4 : 3;

    private string ControlLabel(int index)
    {
        var controls = _snapshot.Controls;
        return index switch
        {
            MicControl => MicLabel,
            AttachControl => AttachLabel,
            0 => "Model: " + (controls?.ModelLabel ?? "Loading…"),
            1 => "Effort: " + (controls?.EffortLabel ?? "Loading…"),
            _ => "Fast: " + (controls is null ? "Loading…" : controls.UltrafastMode ? "Ultra" : controls.FastMode ? "On" : controls.FastAvailable ? "Off" : "Unavailable"),
        };
    }

    private bool ControlEnabled(int index) => index == AttachControl ? AttachEnabled : index == MicControl ? MicEnabled : _connected && !_settingPending && !HasPending && !_attachmentPending
        && _snapshot.Controls is { } controls && (index switch
        {
            0 => controls.Models.Length > 0,
            1 => controls.Efforts.Length > 0,
            _ => controls.FastAvailable || controls.FastMode || controls.UltrafastMode,
        });

    private TerminalOption[] ControlOptions(int index) => _snapshot.Controls is not { } controls ? [] : index switch
    {
        0 => controls.Models,
        1 => controls.Efforts,
        _ => controls.FastAvailable ? [new("off", "Off"), new("on", "On")] : [new("off", "Off")],
    };

    private string? ControlValue(int index) => _snapshot.Controls is not { } controls ? null : index switch
    {
        0 => controls.Model,
        1 => controls.Effort,
        _ => controls.UltrafastMode ? "ultra" : controls.FastMode ? "on" : "off",
    };

    // Keep controls visible at narrow widths; grow into one row on a normal console.
    private int LayoutControls(int width)
    {
        _controlHits.Clear();
        if (_snapshot.Controls is null) return 0;
        _compactControls = false;
        var x = 0;
        var row = 3;
        for (var index = 0; index < ControlCount; index++)
        {
            var desired = Math.Min(width, Math.Min(index == 0 ? 38 : 24, CellWidth(ControlLabel(index)) + 6));
            if (x > 0 && x + desired > width) { x = 0; row++; }
            _controlHits.Add(new(index, x, row, desired));
            x += desired + 1;
        }
        var rows = row - 2;
        if (_height >= 10 && rows + 7 + (HasAttachments ? 1 : 0) <= _height) return rows;
        // A short, narrow viewport can wrap the controls past the composer. Keep the fixed rows inside it.
        _compactControls = true;
        _controlHits.Clear();
        var size = width >= ControlCount * 4 - 1 ? 3 : width >= ControlCount * 3 - 1 ? 2 : 1;
        for (var index = 0; index < ControlCount; index++) _controlHits.Add(new(index, index * (size + 1), 3, size));
        return 1;
    }

    private void DrawControls(List<string> frame, int width, int rows)
    {
        for (var row = 3; row < 3 + rows; row++)
        {
            var output = new StringBuilder();
            var x = 0;
            foreach (var hit in _controlHits.Where(hit => hit.Y == row))
            {
                output.Append(' ', hit.X - x);
                var enabled = ControlEnabled(hit.Index);
                var focused = _controlFocus == hit.Index || _openControl == hit.Index || _hoverControl == hit.Index;
                output.Append(hit.Index == MicControl && Dictating ? _snapshot.Speech?.State == "recording" ? Red : Amber : enabled ? Green : Muted);
                if (focused) output.Append("\u001b[7m");
                if (_compactControls)
                {
                    output.Append(hit.Width == 1 ? "MEFRA"[hit.Index].ToString() : hit.Width == 2
                        ? "MEFRA"[hit.Index] + " " : "[" + "MEFRA"[hit.Index] + "]").Append("\u001b[27m");
                    x = hit.X + hit.Width;
                    continue;
                }
                var label = Fit(ControlLabel(hit.Index), Math.Max(1, hit.Width - 6));
                output.Append("[ ").Append(label).Append(' ', Math.Max(0, hit.Width - 6 - CellWidth(label)))
                    .Append(hit.Index >= MicControl ? "   ]" : enabled ? " v ]" : " - ]").Append("\u001b[27m");
                x = hit.X + hit.Width;
            }
            frame.Add(output.Append("\u001b[K").ToString());
        }
    }

    private void OpenControl(int index)
    {
        if (index == MicControl) { ToggleDictation(); return; }
        if (index == AttachControl) { RequestAttachment("pick"); return; }
        _controlFocus = index;
        _overlay = "";
        if (!ControlEnabled(index))
        {
            _openControl = -1;
            _notice = _settingPending ? "Applying settings…" : !_connected ? "VibeCode is disconnected."
                : HasPending ? "Wait for the submitted prompt before changing settings."
                : $"{(index == 1 ? "Effort" : index == 2 ? "Fast mode" : "Models")} unavailable for this model.";
            return;
        }
        _openControl = _openControl == index ? -1 : index;
        var options = ControlOptions(index);
        _menuSelection = Math.Max(0, Array.FindIndex(options, option => option.Value == ControlValue(index)));
        _menuOffset = 0;
        _notice = "For next prompt · arrows choose · Enter applies · Esc cancels";
    }

    private void ApplyControl()
    {
        if (_openControl < 0 || !ControlEnabled(_openControl)) return;
        var options = ControlOptions(_openControl);
        if (_menuSelection < 0 || _menuSelection >= options.Length) return;
        _settingPending = true;
        _notice = "Applying settings…";
        Send(new("configure", Setting: new(ControlNames[_openControl], options[_menuSelection].Value)));
        _openControl = _controlFocus = -1;
        _dirty = true;
    }

    private bool HandleControlKey(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.F2 or ConsoleKey.F3 or ConsoleKey.F4)
        { OpenControl((int)key.Key - (int)ConsoleKey.F2); return true; }
        if (key.Key == ConsoleKey.Tab)
        {
            _openControl = -1;
            _controlFocus = (key.Modifiers & ConsoleModifiers.Shift) != 0
                ? (_controlFocus == -1 ? ControlCount - 1 : _controlFocus - 1) : (_controlFocus == ControlCount - 1 ? -1 : _controlFocus + 1);
            return true;
        }
        if (key.Key == ConsoleKey.Escape && (_openControl >= 0 || _controlFocus >= 0))
        { _openControl = _controlFocus = -1; _notice = ""; return true; }
        if (_openControl >= 0)
        {
            var count = ControlOptions(_openControl).Length;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: _menuSelection = Math.Max(0, _menuSelection - 1); return true;
                case ConsoleKey.DownArrow: _menuSelection = Math.Min(count - 1, _menuSelection + 1); return true;
                case ConsoleKey.Home: _menuSelection = 0; return true;
                case ConsoleKey.End: _menuSelection = count - 1; return true;
                case ConsoleKey.PageUp: _menuSelection = Math.Max(0, _menuSelection - Math.Max(1, _menuVisible)); return true;
                case ConsoleKey.PageDown: _menuSelection = Math.Min(count - 1, _menuSelection + Math.Max(1, _menuVisible)); return true;
                case ConsoleKey.Enter: case ConsoleKey.Spacebar: ApplyControl(); return true;
                case ConsoleKey.LeftArrow: OpenControl((_openControl + 2) % 3); return true;
                case ConsoleKey.RightArrow: OpenControl((_openControl + 1) % 3); return true;
            }
            // Keep typing and paste out of the picker; the existing draft is untouched.
            if ((key.Modifiers & ConsoleModifiers.Control) == 0) return true;
            _openControl = _controlFocus = -1;
        }
        if (_controlFocus >= 0)
        {
            if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.DownArrow)
            { OpenControl(_controlFocus); return true; }
            _controlFocus = -1;
        }
        return false;
    }

    private void HandleMouse(int x, int y, uint buttons, uint flags)
    {
        var pressed = (buttons & 1) != 0 && (_mouseButtons & 1) == 0;
        _mouseButtons = buttons & 0xffff;
        var hit = _controlHits.FirstOrDefault(hit => y == hit.Y && x >= hit.X && x < hit.X + hit.Width);
        var hover = hit?.Index ?? -1;
        if (_hoverControl != hover) { _hoverControl = hover; _dirty = true; }
        if (flags == 4)
        {
            var delta = (short)(buttons >> 16) > 0 ? -1 : 1;
            if (_openControl >= 0) _menuSelection = Math.Clamp(_menuSelection + delta, 0, Math.Max(0, ControlOptions(_openControl).Length - 1));
            else _scroll = Math.Max(0, _scroll - delta * 3);
            _dirty = true;
            return;
        }
        if (!pressed || flags != 0) return;
        _dirty = true;
        // A dropdown can cover controls wrapped onto the next line; its painted surface wins the hit test.
        if (_openControl >= 0 && x >= _menuX && x < _menuX + _menuWidth && y >= _menuY && y <= _menuY + _menuVisible + 1)
        {
            if (y > _menuY && y <= _menuY + _menuVisible)
            { _menuSelection = _menuOffset + y - _menuY - 1; ApplyControl(); }
            return;
        }
        if (hit is not null) { OpenControl(hit.Index); return; }
        _openControl = _controlFocus = -1;
    }

    private void DrawControlMenu(List<string> frame, int width)
    {
        var hit = _controlHits.FirstOrDefault(hit => hit.Index == _openControl);
        if (hit is null) return;
        var options = ControlOptions(_openControl);
        if (options.Length == 0) { _openControl = -1; return; }
        _menuSelection = Math.Clamp(_menuSelection, 0, options.Length - 1);
        _menuVisible = Math.Min(options.Length, Math.Max(1, Math.Min(12, _height - hit.Y - 5)));
        _menuOffset = Math.Clamp(_menuOffset, Math.Max(0, _menuSelection - _menuVisible + 1), Math.Min(_menuSelection, options.Length - _menuVisible));
        _menuWidth = Math.Min(width, Math.Max(26, Math.Max(hit.Width, Math.Min(52, options.Max(option => CellWidth(option.Label)) + 6))));
        _menuX = Math.Min(hit.X, Math.Max(0, width - _menuWidth));
        _menuY = hit.Y + 1;
        void Row(int y, string value, bool selected = false)
        {
            if (y < 0 || y >= frame.Count) return;
            var clean = Fit(value, _menuWidth);
            // The overlay is part of the cached row. Closing or moving it restores the underlying row.
            var output = new StringBuilder(frame[y]);
            output.Append("\u001b[").Append(_menuX + 1).Append('G')
                .Append(selected ? "\u001b[48;2;118;236;161m\u001b[38;2;8;18;12m" : "\u001b[48;2;20;35;26m" + Text)
                .Append(clean).Append(' ', Math.Max(0, _menuWidth - CellWidth(clean))).Append("\u001b[40m");
            frame[y] = output.ToString();
        }
        Row(_menuY, " " + ControlNames[_openControl] + " · next prompt");
        for (var index = 0; index < _menuVisible; index++)
        {
            var option = options[_menuOffset + index];
            Row(_menuY + index + 1, (option.Value == ControlValue(_openControl) ? " * " : "   ") + option.Label, _menuOffset + index == _menuSelection);
        }
        Row(_menuY + _menuVisible + 1, $" {_menuSelection + 1}/{options.Length}  Enter · Esc");
    }
}
