using Microsoft.AspNetCore.Components;

namespace ControleTecnico;

// Conjunto central de ícones (linha, 24x24) usados no menu, cards e telas.
// Mantém consistência visual e evita repetir SVG nas páginas.
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["executive"] = "<rect x='3' y='3' width='7' height='9'/><rect x='14' y='3' width='7' height='5'/><rect x='14' y='12' width='7' height='9'/><rect x='3' y='16' width='7' height='5'/>",
        ["forecast"] = "<path d='M3 3v18h18'/><path d='m19 9-5 5-4-4-3 3'/>",
        ["pipeline"] = "<polygon points='22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3'/>",
        ["opportunity"] = "<circle cx='12' cy='12' r='9'/><circle cx='12' cy='12' r='5'/><circle cx='12' cy='12' r='1'/>",
        ["customers"] = "<path d='M3 21h18'/><path d='M5 21V7l8-4v18'/><path d='M19 21V11l-6-4'/><path d='M9 9v.01M9 12v.01M9 15v.01M9 18v.01'/>",
        ["analysis"] = "<path d='M21.21 15.89A10 10 0 1 1 8 2.83'/><path d='M22 12A10 10 0 0 0 12 2v10z'/>",
        ["review"] = "<path d='M9 11l3 3L22 4'/><path d='M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11'/>",
        ["history"] = "<path d='M3 3v5h5'/><path d='M3.05 13A9 9 0 1 0 6 5.3L3 8'/><path d='M12 7v5l4 2'/>",
        ["import"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='17 8 12 3 7 8'/><path d='M12 3v12'/>",
        ["master"] = "<ellipse cx='12' cy='5' rx='9' ry='3'/><path d='M3 5v14a9 3 0 0 0 18 0V5'/><path d='M3 12a9 3 0 0 0 18 0'/>",
        ["admin"] = "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/>",
        ["search"] = "<circle cx='11' cy='11' r='8'/><path d='m21 21-4.35-4.35'/>",
        ["bell"] = "<path d='M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9'/><path d='M13.73 21a2 2 0 0 1-3.46 0'/>",
        ["logout"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><polyline points='16 17 21 12 16 7'/><line x1='21' y1='12' x2='9' y2='12'/>",
        ["chevron-left"] = "<polyline points='15 18 9 12 15 6'/>",
        ["chevron-right"] = "<polyline points='9 18 15 12 9 6'/>",
        ["info"] = "<circle cx='12' cy='12' r='10'/><line x1='12' y1='16' x2='12' y2='12'/><line x1='12' y1='8' x2='12.01' y2='8'/>",
        ["up"] = "<line x1='12' y1='19' x2='12' y2='5'/><polyline points='5 12 12 5 19 12'/>",
        ["down"] = "<line x1='12' y1='5' x2='12' y2='19'/><polyline points='19 12 12 19 5 12'/>",
        ["alert"] = "<path d='M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z'/><line x1='12' y1='9' x2='12' y2='13'/><line x1='12' y1='17' x2='12.01' y2='17'/>",
        ["clock"] = "<circle cx='12' cy='12' r='10'/><polyline points='12 6 12 12 16 14'/>",
        ["calendar"] = "<rect x='3' y='4' width='18' height='18' rx='2'/><line x1='16' y1='2' x2='16' y2='6'/><line x1='8' y1='2' x2='8' y2='6'/><line x1='3' y1='10' x2='21' y2='10'/>",
        ["dollar"] = "<line x1='12' y1='1' x2='12' y2='23'/><path d='M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6'/>",
        ["globe"] = "<circle cx='12' cy='12' r='10'/><line x1='2' y1='12' x2='22' y2='12'/><path d='M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z'/>",
        ["users"] = "<path d='M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M23 21v-2a4 4 0 0 0-3-3.87'/><path d='M16 3.13a4 4 0 0 1 0 7.75'/>",
        ["mail"] = "<rect x='2' y='4' width='20' height='16' rx='2'/><path d='m22 7-10 6L2 7'/>",
        ["trend"] = "<polyline points='23 6 13.5 15.5 8.5 10.5 1 18'/><polyline points='17 6 23 6 23 12'/>",
        ["target"] = "<circle cx='12' cy='12' r='10'/><circle cx='12' cy='12' r='6'/><circle cx='12' cy='12' r='2'/>",
        ["download"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='7 10 12 15 17 10'/><line x1='12' y1='15' x2='12' y2='3'/>",
        ["filter"] = "<polygon points='22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3'/>",
        ["check"] = "<polyline points='20 6 9 17 4 12'/>",
        ["x"] = "<line x1='18' y1='6' x2='6' y2='18'/><line x1='6' y1='6' x2='18' y2='18'/>",
        ["edit"] = "<path d='M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7'/><path d='M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4Z'/>",
        ["trash"] = "<polyline points='3 6 5 6 21 6'/><path d='M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2'/><line x1='10' y1='11' x2='10' y2='17'/><line x1='14' y1='11' x2='14' y2='17'/>",
        ["copy"] = "<rect x='9' y='9' width='13' height='13' rx='2' ry='2'/><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'/>",
        ["lock"] = "<rect x='3' y='11' width='18' height='11' rx='2' ry='2'/><path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
        ["play"] = "<polygon points='5 3 19 12 5 21 5 3'/>",
        ["pause"] = "<rect x='6' y='4' width='4' height='16' rx='1'/><rect x='14' y='4' width='4' height='16' rx='1'/>",
        ["arrow-right"] = "<line x1='5' y1='12' x2='19' y2='12'/><polyline points='12 5 19 12 12 19'/>",
        ["expand"] = "<polyline points='15 3 21 3 21 9'/><polyline points='9 21 3 21 3 15'/><line x1='21' y1='3' x2='14' y2='10'/><line x1='3' y1='21' x2='10' y2='14'/>",
        ["layers"] = "<polygon points='12 2 2 7 12 12 22 7 12 2'/><polyline points='2 17 12 22 22 17'/><polyline points='2 12 12 17 22 12'/>",
        ["flag"] = "<path d='M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z'/><line x1='4' y1='22' x2='4' y2='15'/>",
        ["mic"] = "<path d='M12 1a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z'/><path d='M19 10v1a7 7 0 0 1-14 0v-1'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='8' y1='22' x2='16' y2='22'/>",
        ["more"] = "<circle cx='12' cy='5' r='1'/><circle cx='12' cy='12' r='1'/><circle cx='12' cy='19' r='1'/>",
        ["plus"] = "<line x1='12' y1='5' x2='12' y2='19'/><line x1='5' y1='12' x2='19' y2='12'/>",
        ["bar-chart"] = "<line x1='12' y1='20' x2='12' y2='10'/><line x1='18' y1='20' x2='18' y2='4'/><line x1='6' y1='20' x2='6' y2='16'/>",
        ["pie"] = "<path d='M21.21 15.89A10 10 0 1 1 8 2.83'/><path d='M22 12A10 10 0 0 0 12 2v10z'/>",
        ["plane"] = "<path d='M17.8 19.2 16 11l3.5-3.5C21 6 21.5 4 21 3c-1-.5-3 0-4.5 1.5L13 8 4.8 6.2c-.5-.1-.9.1-1.1.5l-.3.5c-.2.5-.1 1 .3 1.3L9 12l-2 3H4l-1 1 3 2 2 3 1-1v-3l3-2 3.5 5.3c.3.4.8.5 1.3.3l.5-.2c.4-.3.6-.7.5-1.2z'/>",
        ["car"] = "<path d='M5 17H3v-5l2-5h14l2 5v5h-2'/><circle cx='7.5' cy='17' r='2'/><circle cx='16.5' cy='17' r='2'/><path d='M9.5 17h5M3 12h18'/>",
        ["sun"] = "<circle cx='12' cy='12' r='4'/><path d='M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41'/>",
        ["ban"] = "<circle cx='12' cy='12' r='10'/><line x1='4.93' y1='4.93' x2='19.07' y2='19.07'/>",
        ["help"] = "<circle cx='12' cy='12' r='10'/><path d='M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3'/><line x1='12' y1='17' x2='12.01' y2='17'/>",
        ["coffee"] = "<path d='M18 8h1a4 4 0 0 1 0 8h-1'/><path d='M2 8h16v9a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4V8z'/><line x1='6' y1='1' x2='6' y2='4'/><line x1='10' y1='1' x2='10' y2='4'/><line x1='14' y1='1' x2='14' y2='4'/>",
        ["infinity"] = "<path d='M18.178 8c5.096 0 5.096 8 0 8-5.095 0-7.133-8-12.739-8-4.585 0-4.585 8 0 8 5.606 0 7.644-8 12.74-8z'/>",
        ["map"] = "<polygon points='1 6 1 22 8 18 16 22 23 18 23 2 16 6 8 2 1 6'/><line x1='8' y1='2' x2='8' y2='18'/><line x1='16' y1='6' x2='16' y2='22'/>",
        ["pin"] = "<path d='M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0z'/><circle cx='12' cy='10' r='3'/>",
        ["paperclip"] = "<path d='m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48'/>",
        ["eye"] = "<path d='M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z'/><circle cx='12' cy='12' r='3'/>",
        ["eye-off"] = "<path d='M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24'/><line x1='1' y1='1' x2='23' y2='23'/>",
        ["refresh"] = "<polyline points='23 4 23 10 17 10'/><polyline points='1 20 1 14 7 14'/><path d='M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15'/>",
        ["user"] = "<path d='M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2'/><circle cx='12' cy='7' r='4'/>",
        ["shield"] = "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/><path d='m9 12 2 2 4-4'/>",
        ["file"] = "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/>",
        ["upload"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='17 8 12 3 7 8'/><line x1='12' y1='3' x2='12' y2='15'/>",
        ["wrench"] = "<path d='M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z'/>",
        ["monitor"] = "<rect x='2' y='3' width='20' height='14' rx='2'/><line x1='8' y1='21' x2='16' y2='21'/><line x1='12' y1='17' x2='12' y2='21'/>",
        ["list"] = "<line x1='8' y1='6' x2='21' y2='6'/><line x1='8' y1='12' x2='21' y2='12'/><line x1='8' y1='18' x2='21' y2='18'/><line x1='3' y1='6' x2='3.01' y2='6'/><line x1='3' y1='12' x2='3.01' y2='12'/><line x1='3' y1='18' x2='3.01' y2='18'/>",
        ["route"] = "<circle cx='6' cy='19' r='3'/><path d='M9 19h8.5a3.5 3.5 0 0 0 0-7h-11a3.5 3.5 0 0 1 0-7H15'/><circle cx='18' cy='5' r='3'/>",
        ["history2"] = "<path d='M3 3v5h5'/><path d='M3.05 13A9 9 0 1 0 6 5.3L3 8'/>",
        ["image"] = "<rect x='3' y='3' width='18' height='18' rx='2' ry='2'/><circle cx='8.5' cy='8.5' r='1.5'/><polyline points='21 15 16 10 5 21'/>",
    };

    public static MarkupString Svg(string name, string cssClass = "")
    {
        var inner = Paths.TryGetValue(name, out var p) ? p : Paths["info"];
        var cls = string.IsNullOrEmpty(cssClass) ? "" : $" class=\"{cssClass}\"";
        return new MarkupString(
            $"<svg{cls} viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
            $"stroke-linecap=\"round\" stroke-linejoin=\"round\">{inner}</svg>");
    }
}
