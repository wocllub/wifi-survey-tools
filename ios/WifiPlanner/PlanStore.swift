import CoreGraphics
import Foundation
import SwiftUI
import UIKit

enum PlanTool: Int, CaseIterable, Identifiable {
    case select, placeAp, wall, area, scale, survey

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .select: return "선택"
        case .placeAp: return "AP"
        case .wall: return "벽"
        case .area: return "범위"
        case .scale: return "축척"
        case .survey: return "실측"
        }
    }

    var icon: String {
        switch self {
        case .select: return "hand.point.up.left"
        case .placeAp: return "wifi.router"
        case .wall: return "line.diagonal"
        case .area: return "selection.pin.in.out"
        case .scale: return "ruler"
        case .survey: return "mappin.and.ellipse"
        }
    }

    var hint: String {
        switch self {
        case .select: return "AP를 끌어서 옮기고, 항목을 눌러 선택합니다."
        case .placeAp: return "도면을 눌러 선택한 제품을 놓습니다."
        case .wall: return "도면을 차례로 눌러 벽을 잇습니다."
        case .area: return "건물 내부를 끌어서 분석 범위를 지정합니다."
        case .scale: return "길이를 아는 두 점을 차례로 누릅니다."
        case .survey: return "측정한 위치를 누르고 신호 값을 입력합니다."
        }
    }
}

enum PlanSelection: Equatable {
    case ap(UUID)
    case wall(UUID)
    case point(UUID)
}

struct PlanTransform {
    var scale: CGFloat
    var originX: CGFloat
    var originY: CGFloat

    func screen(_ x: Double, _ y: Double) -> CGPoint {
        CGPoint(x: originX + CGFloat(x) * scale, y: originY + CGFloat(y) * scale)
    }

    func world(_ point: CGPoint) -> (x: Double, y: Double) {
        (Double((point.x - originX) / scale), Double((point.y - originY) / scale))
    }

    static func fit(_ project: Project, size: CGSize) -> PlanTransform {
        let w = CGFloat(project.PlanW)
        let h = CGFloat(project.PlanH)
        let scale = max(0.5, min((size.width - 24) / w, (size.height - 24) / h))
        return PlanTransform(scale: scale, originX: (size.width - w * scale) / 2, originY: (size.height - h * scale) / 2)
    }
}

@MainActor
final class PlanStore: ObservableObject {
    @Published var project = Project()
    @Published var planImage: UIImage? = nil
    @Published var heat: HeatResult? = nil
    @Published var selection: PlanSelection? = nil
    @Published var tool: PlanTool = .select
    @Published var model: ApModel = Catalog.all[0]
    @Published var wallMaterial = 0
    @Published var zoom: CGFloat = 1
    @Published var pan: CGSize = .zero
    @Published var firstPoint: (x: Double, y: Double)? = nil
    @Published var dragArea: CGRect? = nil
    @Published var message = ""
    @Published var pendingScale: Double? = nil
    @Published var pendingSurvey: (x: Double, y: Double)? = nil

    private var undoStack: [Project] = []
    private var heatTask: Task<Void, Never>? = nil

    init() {
        newProject(DemoProject.make())
        message = "예제 도면입니다. 메뉴에서 실제 도면을 불러올 수 있습니다."
    }

    var canUndo: Bool { !undoStack.isEmpty }

    var selectedAp: ApItem? {
        guard case .ap(let id)? = selection else { return nil }
        return project.Aps.first { $0.id == id }
    }

    func transform(size: CGSize) -> PlanTransform {
        let fit = PlanTransform.fit(project, size: size)
        let scale = fit.scale * zoom
        let w = CGFloat(project.PlanW) * scale
        let h = CGFloat(project.PlanH) * scale
        return PlanTransform(scale: scale, originX: (size.width - w) / 2 + pan.width, originY: (size.height - h) / 2 + pan.height)
    }

    // MARK: 변경 관리
    func snapshot() {
        undoStack.append(project)
        if undoStack.count > 40 { undoStack.removeFirst() }
    }

    func undo() {
        guard let last = undoStack.popLast() else { return }
        project = last
        selection = nil
        firstPoint = nil
        recompute()
    }

    func recompute() {
        heatTask?.cancel()
        let copy = project
        guard copy.View != 2 else {
            heat = nil
            return
        }
        heatTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 90_000_000)
            if Task.isCancelled { return }
            let result = await Task.detached(priority: .userInitiated) { RF.compute(copy, mode: copy.View) }.value
            if Task.isCancelled { return }
            self?.heat = result
        }
    }

    func computeNow() {
        heatTask?.cancel()
        heat = project.View == 2 ? nil : RF.compute(project, mode: project.View)
    }

    func setTool(_ newTool: PlanTool) {
        tool = newTool
        firstPoint = nil
        dragArea = nil
        message = newTool.hint
    }

    func newProject(_ newValue: Project, plan: UIImage? = nil) {
        project = newValue
        project.PlanImageBase64 = nil
        planImage = plan
        undoStack.removeAll()
        selection = nil
        firstPoint = nil
        zoom = 1
        pan = .zero
        message = ""
        recompute()
    }

    func updateSelectedAp(_ change: (inout ApItem) -> Void) {
        guard case .ap(let id)? = selection, let index = project.Aps.firstIndex(where: { $0.id == id }) else { return }
        snapshot()
        change(&project.Aps[index])
        recompute()
    }

    func deleteSelection() {
        guard let current = selection else { return }
        snapshot()
        switch current {
        case .ap(let id): project.Aps.removeAll { $0.id == id }
        case .wall(let id): project.Walls.removeAll { $0.id == id }
        case .point(let id): project.Points.removeAll { $0.id == id }
        }
        selection = nil
        recompute()
    }

    func clearArea() {
        snapshot()
        project.HasArea = false
        recompute()
    }

    func clearPoints() {
        snapshot()
        project.Points.removeAll()
        selection = nil
        recompute()
    }

    // MARK: 도면 조작
    private func clamp(_ world: (x: Double, y: Double)) -> (x: Double, y: Double) {
        (min(project.PlanW, max(0, world.x)), min(project.PlanH, max(0, world.y)))
    }

    func hitTest(_ point: CGPoint, _ t: PlanTransform) -> PlanSelection? {
        func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat { hypot(a.x - b.x, a.y - b.y) }
        for ap in project.Aps.reversed() where distance(point, t.screen(ap.X, ap.Y)) <= 24 {
            return .ap(ap.id)
        }
        for item in project.Points.reversed() where distance(point, t.screen(item.X, item.Y)) <= 18 {
            return .point(item.id)
        }
        for wall in project.Walls.reversed() {
            let a = t.screen(wall.X1, wall.Y1)
            let b = t.screen(wall.X2, wall.Y2)
            let dx = b.x - a.x
            let dy = b.y - a.y
            let length = dx * dx + dy * dy
            let k = length == 0 ? 0 : max(0, min(1, ((point.x - a.x) * dx + (point.y - a.y) * dy) / length))
            if distance(point, CGPoint(x: a.x + k * dx, y: a.y + k * dy)) <= 14 {
                return .wall(wall.id)
            }
        }
        return nil
    }

    func tap(at point: CGPoint, _ t: PlanTransform) {
        let w = clamp(t.world(point))
        switch tool {
        case .select:
            selection = hitTest(point, t)
        case .placeAp:
            placeAp(x: w.x, y: w.y)
        case .wall:
            if let first = firstPoint, hypot(first.x - w.x, first.y - w.y) > 0.05 {
                snapshot()
                project.Walls.append(WallItem(X1: first.x, Y1: first.y, X2: w.x, Y2: w.y, Material: wallMaterial))
                recompute()
            }
            firstPoint = w
        case .area:
            break
        case .scale:
            if let first = firstPoint {
                firstPoint = nil
                let measured = hypot(first.x - w.x, first.y - w.y)
                if measured >= 0.01 { pendingScale = measured }
            } else {
                firstPoint = w
            }
        case .survey:
            pendingSurvey = w
        }
    }

    func placeAp(x: Double, y: Double) {
        snapshot()
        let count = project.Aps.filter { $0.Model == model.name }.count + 1
        let ap = model.makeAp(name: "\(model.name)-\(count)", x: x, y: y)
        project.Aps.append(ap)
        selection = .ap(ap.id)
        recompute()
    }

    func moveAp(id: UUID, to point: CGPoint, _ t: PlanTransform) {
        guard let index = project.Aps.firstIndex(where: { $0.id == id }) else { return }
        let w = clamp(t.world(point))
        project.Aps[index].X = w.x
        project.Aps[index].Y = w.y
        recompute()
    }

    func finishArea(from a: CGPoint, to b: CGPoint, _ t: PlanTransform) {
        let p = clamp(t.world(a))
        let q = clamp(t.world(b))
        dragArea = nil
        let w = abs(p.x - q.x)
        let h = abs(p.y - q.y)
        guard w > 0.5, h > 0.5 else { return }
        snapshot()
        project.HasArea = true
        project.AreaX = min(p.x, q.x)
        project.AreaY = min(p.y, q.y)
        project.AreaW = w
        project.AreaH = h
        recompute()
    }

    func applyScale(measured: Double, real: Double) {
        guard measured > 0, real > 0 else { return }
        snapshot()
        let f = real / measured
        for i in project.Aps.indices {
            project.Aps[i].X *= f
            project.Aps[i].Y *= f
        }
        for i in project.Walls.indices {
            project.Walls[i].X1 *= f
            project.Walls[i].Y1 *= f
            project.Walls[i].X2 *= f
            project.Walls[i].Y2 *= f
        }
        for i in project.Points.indices {
            project.Points[i].X *= f
            project.Points[i].Y *= f
        }
        project.AreaX *= f
        project.AreaY *= f
        project.AreaW *= f
        project.AreaH *= f
        project.PlanW *= f
        project.PlanH *= f
        project.PixelsPerMeter /= f
        project.ScaleSet = true
        zoom = 1
        pan = .zero
        message = String(format: "도면 크기 %.1f × %.1f m", project.PlanW, project.PlanH)
        recompute()
    }

    func addSurvey(x: Double, y: Double, ssid: String, band: Int, rssi: Int) {
        snapshot()
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        let name = ssid.trimmingCharacters(in: .whitespacesAndNewlines)
        let first = project.Points.isEmpty
        project.Points.append(SurveyPoint(X: x, Y: y, Time: formatter.string(from: Date()),
                                          Readings: [Reading(Ssid: name, Bssid: "", Band: band, Rssi: rssi)]))
        if first {
            project.SurveySsid = name
            project.Band = band
            project.View = 1
        }
        message = "실측 지점을 추가했습니다."
        recompute()
    }

    var surveySsids: [String] {
        Array(Set(project.Points.flatMap { $0.Readings.map { $0.Ssid } })).sorted()
    }

    var predictionError: Double? {
        guard !project.Aps.isEmpty else { return nil }
        var diffs: [Double] = []
        for point in project.Points {
            let measured = RF.pointValue(project, point)
            if measured <= RF.noSignal { continue }
            let predicted = RF.best(project, x: point.X, y: point.Y, band: project.Band).value
            if predicted.isFinite { diffs.append(abs(predicted - measured)) }
        }
        return diffs.isEmpty ? nil : diffs.reduce(0, +) / Double(diffs.count)
    }

    // MARK: 파일
    func loadPlan(data: Data) throws {
        guard let source = UIImage(data: data) else { throw PlanFileError.badImage }
        let image = Self.limited(source, maxSide: 5000)
        undoStack.removeAll()
        planImage = image
        project.PixelsPerMeter = 20
        project.ScaleSet = false
        project.HasArea = false
        project.PlanW = Double(image.size.width) / project.PixelsPerMeter
        project.PlanH = Double(image.size.height) / project.PixelsPerMeter
        zoom = 1
        pan = .zero
        setTool(.scale)
        message = "길이를 아는 두 점을 눌러 축척을 설정해 주세요."
        recompute()
    }

    private static func limited(_ image: UIImage, maxSide: CGFloat) -> UIImage {
        let side = max(image.size.width, image.size.height)
        let k = min(1, maxSide / side)
        let size = CGSize(width: (image.size.width * k).rounded(), height: (image.size.height * k).rounded())
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        return UIGraphicsImageRenderer(size: size, format: format).image { _ in
            image.draw(in: CGRect(origin: .zero, size: size))
        }
    }

    func loadProject(data: Data) throws {
        let decoded = try JSONDecoder().decode(Project.self, from: Self.withoutBOM(data))
        var plan: UIImage? = nil
        if let encoded = decoded.PlanImageBase64, !encoded.isEmpty {
            guard let bytes = Data(base64Encoded: encoded), let image = UIImage(data: bytes) else {
                throw PlanFileError.badImage
            }
            plan = image
        }
        newProject(decoded, plan: plan)
    }

    private static func withoutBOM(_ data: Data) -> Data {
        data.starts(with: [0xEF, 0xBB, 0xBF]) ? data.dropFirst(3) : data
    }

    func projectData() throws -> Data {
        var copy = project
        copy.PlanImageBase64 = planImage?.pngData()?.base64EncodedString()
        return try JSONEncoder().encode(copy)
    }

    func surveyCSV() -> Data {
        var lines = ["지점,측정시각,X(m),Y(m),SSID,BSSID,대역,신호(dBm)"]
        for (index, point) in project.Points.enumerated() {
            for reading in point.Readings {
                let x = String(format: "%.2f", point.X)
                let y = String(format: "%.2f", point.Y)
                lines.append("\(index + 1),\(point.Time),\(x),\(y),\(Self.csvField(reading.Ssid)),\(reading.Bssid),\(RF.bandNames[reading.Band]),\(reading.Rssi)")
            }
        }
        return Data([0xEF, 0xBB, 0xBF]) + Data((lines.joined(separator: "\r\n") + "\r\n").utf8)
    }

    /// SSID가 스프레드시트 수식으로 해석되지 않도록 막는다.
    static func csvField(_ value: String) -> String {
        var text = value
        if let first = text.first, "=+-@".contains(first) { text = "'" + text }
        return "\"" + text.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }
}

enum PlanFileError: LocalizedError {
    case badImage

    var errorDescription: String? { "이미지를 읽을 수 없습니다." }
}
