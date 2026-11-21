import XCTest
@testable import WifiPlanner

final class RadioModelTests: XCTestCase {
    private func sampleProject() -> (Project, ApItem) {
        var p = Project()
        p.Exponent = 2
        p.PlanW = 40
        p.PlanH = 20
        let ap = ApItem(Name: "A", X: 5, Y: 10, Height: 1, Tx5: 18, Gain5: 5)
        p.Aps.append(ap)
        return (p, ap)
    }

    func testPathLossMatchesOtherVersions() {
        let (p, ap) = sampleProject()
        XCTAssertEqual(RF.signal(p, ap, x: 15, y: 10, band: 1), -44.26, accuracy: 0.05)
        XCTAssertEqual(RF.signal(p, ap, x: 5, y: 10, band: 1), 23 - 47.26, accuracy: 0.05)
    }

    func testWallLossOnlyBehindWall() {
        var (p, ap) = sampleProject()
        let open = RF.signal(p, ap, x: 15, y: 10, band: 1)
        let near = RF.signal(p, ap, x: 8, y: 10, band: 1)
        let onWallBefore = RF.signal(p, ap, x: 10, y: 10, band: 1)
        p.Walls.append(WallItem(X1: 10, Y1: 0, X2: 10, Y2: 20, Material: 4))
        XCTAssertEqual(open - RF.signal(p, ap, x: 15, y: 10, band: 1), 20, accuracy: 0.0001)
        XCTAssertEqual(near, RF.signal(p, ap, x: 8, y: 10, band: 1), accuracy: 0.0001)
        XCTAssertEqual(onWallBefore, RF.signal(p, ap, x: 10, y: 10, band: 1), accuracy: 0.0001)
    }

    func testSixGhzRequiresSupport() {
        let (p, ap) = sampleProject()
        XCTAssertEqual(RF.signal(p, ap, x: 15, y: 10, band: 2), -Double.infinity)
    }

    func testDemoMatchesOtherVersions() {
        let heat = RF.compute(DemoProject.make(), mode: 0)
        XCTAssertEqual(heat.coveragePct, 96.0, accuracy: 0.05)
        XCTAssertEqual(heat.deadM2, 46.3, accuracy: 0.15)
        XCTAssertEqual(heat.covered + heat.dead, heat.analyzed)
        XCTAssertNotNil(heat.image)
        let runArea = heat.deadRuns.reduce(0.0) { $0 + Double($1.width * $1.height) }
        XCTAssertEqual(runArea, heat.deadM2, accuracy: 0.001)
    }

    func testAreaLimitsAnalysis() {
        var p = Project()
        p.PlanW = 40
        p.PlanH = 20
        p.HasArea = true
        p.AreaW = 20
        p.AreaH = 20
        p.Threshold = -55
        p.Aps.append(ApItem(X: 2, Y: 2, Tx5: 18, Gain5: 5))
        let heat = RF.compute(p, mode: 0)
        XCTAssertEqual(heat.analyzedM2, 400, accuracy: 12)
        XCTAssertEqual(heat.coveragePct, 60.7, accuracy: 0.1)
    }

    func testSurveyValues() {
        var p = Project()
        p.SurveySsid = "X"
        let hit = SurveyPoint(X: 5, Y: 5, Readings: [Reading(Ssid: "X", Band: 1, Rssi: -50), Reading(Ssid: "Y", Band: 1, Rssi: -30)])
        let miss = SurveyPoint(X: 9, Y: 5, Readings: [Reading(Ssid: "Y", Band: 1, Rssi: -40)])
        XCTAssertEqual(RF.pointValue(p, hit), -50)
        XCTAssertEqual(RF.pointValue(p, miss), RF.noSignal)
    }

    func testInterpolation() {
        let points = [(x: 5.0, y: 5.0, value: -50.0), (x: 9.0, y: 5.0, value: -90.0)]
        XCTAssertEqual(RF.idw(points, x: 5, y: 5), -50, accuracy: 0.001)
        XCTAssertEqual(RF.idw(points, x: 7, y: 5), -70, accuracy: 0.001)
        XCTAssertTrue(RF.idw(points, x: 30, y: 15).isNaN)
    }
}

final class ProjectFileTests: XCTestCase {
    func testOpensFileFromOtherVersions() throws {
        let json = """
        {"PlanImageBase64":null,"PixelsPerMeter":20,"PlanW":48.5,"PlanH":30,"ScaleSet":true,"HasArea":false,
        "AreaX":0,"AreaY":0,"AreaW":0,"AreaH":0,"Threshold":-67,"Exponent":2.6,"Band":1,"View":0,"SurveySsid":"사무실",
        "Aps":[{"Vendor":"Cisco","Model":"CW9166I","Name":"AP1","X":12,"Y":15.5,"Height":3,"Tx24":15,"Tx5":18,"Tx6":18,
        "Gain24":3,"Gain5":5,"Gain6":4,"Has6":true}],
        "Walls":[{"X1":1,"Y1":2,"X2":3,"Y2":4,"Material":4}],
        "Points":[{"X":1,"Y":2,"Time":"2026-11-18 12:00:00","Readings":[{"Ssid":"사무실","Bssid":"AA:BB","Band":1,"Rssi":-55}]}]}
        """
        let p = try JSONDecoder().decode(Project.self, from: Data(json.utf8))
        XCTAssertEqual(p.PlanW, 48.5)
        XCTAssertEqual(p.Aps.first?.Model, "CW9166I")
        XCTAssertEqual(p.Walls.first?.Material, 4)
        XCTAssertEqual(p.Points.first?.Readings.first?.Rssi, -55)
    }

    func testWrittenFileUsesSharedFieldNames() throws {
        let data = try JSONEncoder().encode(DemoProject.make())
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let aps = try XCTUnwrap(object["Aps"] as? [[String: Any]])
        XCTAssertEqual(aps.count, 3)
        XCTAssertNotNil(aps[0]["Gain5"])
        XCTAssertNil(aps[0]["id"])
    }
}

@MainActor
final class PlanStoreTests: XCTestCase {
    private let t = PlanTransform(scale: 10, originX: 0, originY: 0)

    private func emptyStore() -> PlanStore {
        let store = PlanStore()
        store.newProject(Project())
        return store
    }

    func testPlaceWallUndoAndDelete() {
        let store = emptyStore()
        store.setTool(.placeAp)
        store.tap(at: CGPoint(x: 100, y: 100), t)
        XCTAssertEqual(store.project.Aps.count, 1)
        store.setTool(.wall)
        store.tap(at: CGPoint(x: 200, y: 20), t)
        store.tap(at: CGPoint(x: 200, y: 220), t)
        store.tap(at: CGPoint(x: 300, y: 220), t)
        XCTAssertEqual(store.project.Walls.count, 2)
        store.undo()
        XCTAssertEqual(store.project.Walls.count, 1)
        store.setTool(.select)
        store.tap(at: CGPoint(x: 200, y: 120), t)
        XCTAssertEqual(store.selection, .wall(store.project.Walls[0].id))
        store.deleteSelection()
        XCTAssertTrue(store.project.Walls.isEmpty)
    }

    func testScaleConvertsCoordinates() {
        let store = emptyStore()
        store.placeAp(x: 10, y: 5)
        store.applyScale(measured: 10, real: 20)
        XCTAssertEqual(store.project.PlanW, 80, accuracy: 0.001)
        XCTAssertEqual(store.project.Aps[0].X, 20, accuracy: 0.001)
    }

    func testManualSurveySwitchesToMeasuredView() {
        let store = emptyStore()
        store.addSurvey(x: 5, y: 5, ssid: " Office ", band: 0, rssi: -72)
        XCTAssertEqual(store.project.View, 1)
        XCTAssertEqual(store.project.SurveySsid, "Office")
        XCTAssertEqual(store.project.Band, 0)
        store.computeNow()
        XCTAssertEqual(store.heat?.analyzed ?? 0 > 0, true)
        XCTAssertEqual(store.heat?.coveragePct ?? -1, 0, accuracy: 0.001)
    }

    func testProjectRoundTrip() throws {
        let store = PlanStore()
        let data = try store.projectData()
        let other = emptyStore()
        try other.loadProject(data: data)
        XCTAssertEqual(other.project.Aps.count, 3)
        XCTAssertEqual(other.project.Walls.count, 24)
    }

    func testCsvProtectsFormulas() {
        XCTAssertEqual(PlanStore.csvField("=A1"), "\"'=A1\"")
        XCTAssertEqual(PlanStore.csvField("a\"b"), "\"a\"\"b\"")
    }
}


final class ReportTests: XCTestCase {
    @MainActor
    func testReportEscapesAPNamesAndIncludesCoverageImage() throws {
        let store = PlanStore()
        var project = DemoProject.make()
        project.Aps[0].Name = "=<example>"
        store.newProject(project)
        store.computeNow()
        let csv = try XCTUnwrap(String(data: store.accessPointCSV(), encoding: .utf8))
        XCTAssertTrue(csv.contains("\"'=<example>\""))
        let report = try XCTUnwrap(String(data: store.reportHTML(coverageImage: "aW1hZ2U="), encoding: .utf8))
        XCTAssertTrue(report.contains("=&lt;example&gt;"))
        XCTAssertTrue(report.contains("data:image/png;base64,aW1hZ2U="))
        XCTAssertTrue(report.contains("커버리지"))
    }
}
