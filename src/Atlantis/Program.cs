using Atlantis;

#if IOS
using Atlantis.Platforms.iOS;
iOSApp.Run(HelloPage.Html);
#else
// Desktop implementation using Photino
using Photino.NET;

var window = new PhotinoWindow()
    .SetTitle("Atlantis")
    .SetSize(800, 600)
    .Center()
    .LoadRawString(HelloPage.Html);

window.WaitForClose();
#endif
