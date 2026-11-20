using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal struct Binding
{
    internal Keys Key;
    internal uint Mods;
    internal Binding(Keys key, uint mods) { Key = key; Mods = mods; }
    internal bool Held()
    {
        return Native.Down(Key) && ((Mods & 1) == 0 || Native.Down(Keys.Menu)) &&
            ((Mods & 2) == 0 || Native.Down(Keys.ControlKey)) && ((Mods & 4) == 0 || Native.Down(Keys.ShiftKey)) &&
            ((Mods & 8) == 0 || Native.Down(Keys.LWin) || Native.Down(Keys.RWin));
    }
    public override string ToString()
    {
        return ((Mods & 2) != 0 ? "Ctrl + " : "") + ((Mods & 1) != 0 ? "Alt + " : "") +
            ((Mods & 4) != 0 ? "Shift + " : "") + ((Mods & 8) != 0 ? "Win + " : "") + KeyName(Key);
    }
    internal static string KeyName(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9) return ((int)key - (int)Keys.D0).ToString();
        return key == Keys.Return ? "Enter" : key.ToString();
    }
    internal static bool ValidKey(Keys key)
    {
        int k = (int)key;
        return k >= 8 && k <= 254 && Enum.IsDefined(typeof(Keys), key) &&
            key != Keys.ShiftKey && key != Keys.Menu && key != Keys.ControlKey &&
            key != Keys.LWin && key != Keys.RWin && !(k >= 160 && k <= 165);
    }
}

internal sealed class Preferences
{
    internal Binding Shortcut = new Binding(Keys.F8, 0);
    // -1 沿用自动位置，0 到 8 从左上到右下。
    internal int Position = -1;
    private static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BorrowScreen.ini");
    internal static Preferences Load()
    {
        Preferences result = new Preferences();
        try
        {
            if (!File.Exists(FilePath)) return result;
            string[] lines = File.ReadAllLines(FilePath);
            int key, pos; uint mods;
            if (lines.Length == 3 && int.TryParse(lines[0], out key) && Binding.ValidKey((Keys)key) &&
                uint.TryParse(lines[1], out mods) && mods <= 15 && int.TryParse(lines[2], out pos) && pos >= -1 && pos <= 8)
            { result.Shortcut = new Binding((Keys)key, mods); result.Position = pos; }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }
    internal void Save()
    {
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temp, new string[] { ((int)Shortcut.Key).ToString(), Shortcut.Mods.ToString(), Position.ToString() });
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class SettingsForm : Form
{
    private readonly ComboBox keyList = new ComboBox();
    private readonly CheckBox[] modifiers = new CheckBox[4];
    private readonly uint[] masks = { 2, 1, 4, 8 };
    private readonly Button record = new Button();
    private readonly Label status = new Label();
    private readonly RadioButton auto = new RadioButton();
    private readonly RadioButton[] places = new RadioButton[9];
    private bool recording;
    private static readonly Color Accent = Color.FromArgb(0, 100, 102);
    internal SettingsForm(Binding binding, int position, Func<Preferences,string> apply)
    {
        Text = "余光漫游设置";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(448, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Label heading = Caption("触发快捷键", 24, 16, 220, 26);
        heading.Font = new Font(Font, FontStyle.Bold);
        string[] names = { "Ctrl", "Alt", "Shift", "Win" };
        for (int i=0;i<4;i++)
        {
            modifiers[i] = new CheckBox { Text=names[i], AutoSize=true, Location=new Point(25+i*66,53),
                Checked=(binding.Mods & masks[i]) != 0 };
            Controls.Add(modifiers[i]);
        }
        keyList.SetBounds(24, 86, 224, 30); keyList.DropDownStyle=ComboBoxStyle.DropDownList;
        keyList.AccessibleName="主键"; keyList.FormattingEnabled=true;
        keyList.Format += delegate(object sender,ListControlConvertEventArgs e) { e.Value=Binding.KeyName((Keys)e.ListItem); };
        for(int k=8;k<=254;k++) if(Binding.ValidKey((Keys)k)) keyList.Items.Add((Keys)k);
        keyList.SelectedItem=binding.Key; Controls.Add(keyList);
        record.Text="录入快捷键"; record.SetBounds(260,84,162,32); Controls.Add(record);
        record.Click+=delegate { recording=!recording; record.Text=recording?"请按快捷键…":"录入快捷键"; status.Text=""; };
        record.Leave+=delegate { recording=false; record.Text="录入快捷键"; };
        Label positionHeading=Caption("浮窗位置",24,130,125,24); positionHeading.Font=new Font(Font,FontStyle.Bold);
        auto.Text="自动位置"; auto.SetBounds(323,129,100,26); Controls.Add(auto);
        string[] labels={"左上","中上","右上","左","中","右","左下","中下","右下"};
        for(int i=0;i<9;i++)
        {
            RadioButton b=new RadioButton { Appearance=Appearance.Button, Text=labels[i],
                TextAlign=ContentAlignment.MiddleCenter, FlatStyle=FlatStyle.Flat, UseVisualStyleBackColor=false };
            b.SetBounds(24+(i%3)*135,166+(i/3)*44,128,38);
            b.FlatAppearance.BorderColor=Color.FromArgb(194,210,209);
            b.FlatAppearance.CheckedBackColor=Accent; b.BackColor=Color.FromArgb(246,249,249);
            b.CheckedChanged+=delegate { b.ForeColor=b.Checked?Color.White:SystemColors.ControlText; };
            places[i]=b; Controls.Add(b);
        }
        if(position<0) auto.Checked=true; else places[position].Checked=true;
        Caption("位置以当前输入窗口所在屏幕的工作区为准。",24,302,401,25);
        status.SetBounds(24,330,400,37); status.ForeColor=Color.Firebrick; Controls.Add(status);
        Button cancel=new Button { Text="取消",DialogResult=DialogResult.Cancel }; cancel.SetBounds(218,371,95,33);
        Button save=new Button { Text="保存",FlatStyle=FlatStyle.Flat,BackColor=Accent,ForeColor=Color.White };
        save.SetBounds(327,371,95,33); Controls.Add(cancel); Controls.Add(save);
        cancel.Click+=delegate { Close(); }; CancelButton=cancel; AcceptButton=save;
        save.Click+=delegate {
            uint mods=0; for(int i=0;i<4;i++) if(modifiers[i].Checked) mods|=masks[i];
            var prefs=new Preferences { Shortcut=new Binding((Keys)keyList.SelectedItem,mods),
                Position=Array.FindIndex(places,delegate(RadioButton b){return b.Checked;}) };
            string error=apply(prefs); if(error==null) Close(); else status.Text=error;
        };
    }
    private Label Caption(string text,int x,int y,int w,int h)
    {
        var label=new Label { Text=text,TextAlign=ContentAlignment.MiddleLeft };
        label.SetBounds(x,y,w,h); Controls.Add(label); return label;
    }
    internal void Recorded(Binding value)
    {
        if(!recording) return;
        keyList.SelectedItem=value.Key;
        for(int i=0;i<4;i++) modifiers[i].Checked=(value.Mods&masks[i])!=0;
        recording=false; record.Text="录入快捷键"; status.Text="";
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if(!recording) return base.ProcessCmdKey(ref msg,keyData);
        Keys key=keyData & Keys.KeyCode;
        if(Binding.ValidKey(key))
        {
            uint mods=(uint)(((keyData&Keys.Control)!=0?2:0)|((keyData&Keys.Alt)!=0?1:0)|((keyData&Keys.Shift)!=0?4:0)|
                (Native.Down(Keys.LWin)||Native.Down(Keys.RWin)?8:0));
            Recorded(new Binding(key,mods));
        }
        return true;
    }
}
