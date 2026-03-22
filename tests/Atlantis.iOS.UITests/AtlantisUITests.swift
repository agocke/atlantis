import XCTest

struct TestError: Error {
    let message: String
}

final class AtlantisUITests: XCTestCase {
    var app: XCUIApplication!
    
    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.atlantis.app")
        app.launch()
    }
    
    override func tearDownWithError() throws {
        app.terminate()
    }
    
    func testAppLaunches() throws {
        // Verify the app launched successfully
        let launched = app.wait(for: .runningForeground, timeout: 5)
        if !launched {
            throw TestError(message: "App did not launch")
        }
    }
    
    func testWebViewExists() throws {
        // WKWebView appears as a webView in the accessibility hierarchy
        let webView = app.webViews.firstMatch
        if !webView.waitForExistence(timeout: 5) {
            throw TestError(message: "WebView should exist")
        }
    }
    
    func testHelloWorldContent() throws {
        // Check that the Hello World text is visible in the webview
        let webView = app.webViews.firstMatch
        if !webView.waitForExistence(timeout: 5) {
            throw TestError(message: "WebView should exist")
        }
        
        // Static text within web content
        let helloText = webView.staticTexts["Hello, World!"]
        if !helloText.waitForExistence(timeout: 5) {
            throw TestError(message: "Hello, World! text should be visible")
        }
    }
}
