import SwiftUI
import WebKit

// MARK: - Shared State

private var pendingHtml: String = ""

// MARK: - C Entry Point

/// Entry point called from C#/.NET - runs the SwiftUI app
/// - Parameter html: UTF-8 C string containing HTML to display
@_cdecl("atlantis_run")
public func atlantisRun(_ html: UnsafePointer<CChar>) {
    pendingHtml = String(cString: html)
    
    // Run the app using UIApplicationMain (avoiding @main conflict with .NET)
    let argc = CommandLine.argc
    let argv = CommandLine.unsafeArgv
    
    UIApplicationMain(
        argc,
        argv,
        nil,
        NSStringFromClass(AtlantisAppDelegate.self)
    )
}

// MARK: - App Delegate (Scene-based)

class AtlantisAppDelegate: UIResponder, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        configurationForConnecting connectingSceneSession: UISceneSession,
        options: UIScene.ConnectionOptions
    ) -> UISceneConfiguration {
        let config = UISceneConfiguration(name: nil, sessionRole: connectingSceneSession.role)
        config.delegateClass = AtlantisSceneDelegate.self
        return config
    }
}

class AtlantisSceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?
    
    func scene(
        _ scene: UIScene,
        willConnectTo session: UISceneSession,
        options connectionOptions: UIScene.ConnectionOptions
    ) {
        guard let windowScene = scene as? UIWindowScene else { return }
        
        let window = UIWindow(windowScene: windowScene)
        let hostingController = UIHostingController(rootView: ContentView(html: pendingHtml))
        window.rootViewController = hostingController
        window.makeKeyAndVisible()
        self.window = window
    }
}

// MARK: - WebView

struct ContentView: View {
    let html: String
    @State private var page = WebPage()
    
    var body: some View {
        WebKit.WebView(page)
            .ignoresSafeArea()
            .task {
                page.load(html: html)
            }
    }
}
