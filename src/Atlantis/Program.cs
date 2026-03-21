using Atlantis;
using PhotinoNET;

var window = new PhotinoWindow()
    .SetTitle("Atlantis")
    .SetSize(800, 600)
    .Center()
    .LoadRawString(HelloPage.Html);

window.WaitForClose();
