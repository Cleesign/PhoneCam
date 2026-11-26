import SwiftUI

@main
struct PhoneCamApp: App {
    @StateObject private var model = CameraModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(model)
        }
    }
}
