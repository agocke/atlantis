using Atlantis;
using Photino.NET;

var window = new PhotinoWindow()
    .SetTitle("Atlantis")
    .SetSize(800, 600)
    .Center()
    .LoadRawString(HelloPage.Html);

window.WaitForClose();
