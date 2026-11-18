import Foundation

// 프로젝트 파일(.wifiplan)은 Windows, Linux 버전과 같은 JSON 형식을 사용한다.
struct ApItem: Codable, Identifiable, Equatable {
    var id = UUID()
    var Vendor = ""
    var Model = ""
    var Name = ""
    var X = 0.0
    var Y = 0.0
    var Height = 3.0
    var Tx24 = 15.0
    var Tx5 = 18.0
    var Tx6 = 18.0
    var Gain24 = 4.0
    var Gain5 = 5.0
    var Gain6 = 5.0
    var Has6 = false

    enum CodingKeys: String, CodingKey {
        case Vendor, Model, Name, X, Y, Height, Tx24, Tx5, Tx6, Gain24, Gain5, Gain6, Has6
    }
}

struct WallItem: Codable, Identifiable, Equatable {
    var id = UUID()
    var X1 = 0.0
    var Y1 = 0.0
    var X2 = 0.0
    var Y2 = 0.0
    var Material = 0

    enum CodingKeys: String, CodingKey {
        case X1, Y1, X2, Y2, Material
    }
}

struct Reading: Codable, Equatable {
    var Ssid = ""
    var Bssid = ""
    var Band = 0
    var Rssi = 0
}

struct SurveyPoint: Codable, Identifiable, Equatable {
    var id = UUID()
    var X = 0.0
    var Y = 0.0
    var Time = ""
    var Readings: [Reading] = []

    enum CodingKeys: String, CodingKey {
        case X, Y, Time, Readings
    }
}

struct Project: Codable, Equatable {
    var PlanImageBase64: String? = nil
    var PixelsPerMeter = 20.0
    var PlanW = 40.0
    var PlanH = 25.0
    var ScaleSet = true
    var HasArea = false
    var AreaX = 0.0
    var AreaY = 0.0
    var AreaW = 0.0
    var AreaH = 0.0
    var Threshold = -67.0
    var Exponent = 2.6
    var Band = 1
    var View = 0
    var SurveySsid = ""
    var Aps: [ApItem] = []
    var Walls: [WallItem] = []
    var Points: [SurveyPoint] = []
}

struct ApModel: Identifiable, Equatable {
    let vendor: String
    let name: String
    let gen: String
    let g24: Double
    let g5: Double
    let g6: Double?
    /// true: 제조사 자료로 안테나 이득을 확인한 모델
    let verified: Bool

    var id: String { vendor + "/" + name }
    var has6: Bool { g6 != nil }

    func makeAp(name apName: String, x: Double, y: Double) -> ApItem {
        ApItem(Vendor: vendor, Model: name, Name: apName, X: x, Y: y,
               Gain24: g24, Gain5: g5, Gain6: g6 ?? 0, Has6: has6)
    }
}

enum Catalog {
    static let all: [ApModel] = [
        ApModel(vendor: "Cisco", name: "CW9176I", gen: "Wi-Fi 7", g24: 4, g5: 5, g6: 5, verified: false),
        ApModel(vendor: "Cisco", name: "CW9166I", gen: "Wi-Fi 6E", g24: 3, g5: 5, g6: 4, verified: false),
        ApModel(vendor: "Cisco", name: "C9136I", gen: "Wi-Fi 6E", g24: 4, g5: 6, g6: 6, verified: false),
        ApModel(vendor: "Cisco", name: "C9130AXI", gen: "Wi-Fi 6", g24: 4, g5: 5, g6: nil, verified: false),
        ApModel(vendor: "Cisco", name: "C9120AXI", gen: "Wi-Fi 6", g24: 4, g5: 5, g6: nil, verified: false),
        ApModel(vendor: "HPE Aruba", name: "AP-735", gen: "Wi-Fi 7", g24: 4, g5: 6, g6: 6, verified: false),
        ApModel(vendor: "HPE Aruba", name: "AP-655", gen: "Wi-Fi 6E", g24: 4.8, g5: 5.3, g6: 5.4, verified: false),
        ApModel(vendor: "HPE Aruba", name: "AP-635", gen: "Wi-Fi 6E", g24: 4.6, g5: 7.0, g6: 6.3, verified: true),
        ApModel(vendor: "HPE Aruba", name: "AP-515", gen: "Wi-Fi 6", g24: 4.2, g5: 7.5, g6: nil, verified: true),
        ApModel(vendor: "Juniper Mist", name: "AP47", gen: "Wi-Fi 7", g24: 4, g5: 6, g6: 6, verified: true),
        ApModel(vendor: "Juniper Mist", name: "AP45", gen: "Wi-Fi 6E", g24: 3, g5: 5, g6: 5, verified: true),
        ApModel(vendor: "Juniper Mist", name: "AP34", gen: "Wi-Fi 6E", g24: 4, g5: 6, g6: 6, verified: true),
        ApModel(vendor: "Juniper Mist", name: "AP43", gen: "Wi-Fi 6", g24: 4, g5: 6, g6: nil, verified: true),
        ApModel(vendor: "Juniper Mist", name: "AP32", gen: "Wi-Fi 6", g24: 5, g5: 6, g6: nil, verified: true),
        ApModel(vendor: "기타", name: "일반 AP (직접 입력)", gen: "-", g24: 4, g5: 5, g6: 5, verified: false),
    ]

    static let vendors: [String] = {
        var seen: [String] = []
        for model in all where !seen.contains(model.vendor) {
            seen.append(model.vendor)
        }
        return seen
    }()

    static func mark(for vendor: String) -> String {
        switch vendor {
        case "Cisco": return "C"
        case "HPE Aruba": return "A"
        case "Juniper Mist": return "M"
        default: return "AP"
        }
    }
}

struct WallMaterial {
    let name: String
    /// 대역별(2.4 / 5 / 6 GHz) 통과 손실 dB. 일반적인 설계 추정치.
    let loss: [Double]

    static let all: [WallMaterial] = [
        WallMaterial(name: "석고보드", loss: [3, 4, 5]),
        WallMaterial(name: "유리", loss: [2, 4, 5]),
        WallMaterial(name: "목재 문", loss: [3, 4, 5]),
        WallMaterial(name: "벽돌", loss: [6, 10, 12]),
        WallMaterial(name: "콘크리트", loss: [12, 20, 25]),
        WallMaterial(name: "금속 / 엘리베이터", loss: [25, 30, 35]),
    ]
}

enum DemoProject {
    static func make() -> Project {
        var p = Project()
        p.PlanW = 48
        p.PlanH = 30
        p.HasArea = true
        p.AreaX = 2
        p.AreaY = 2
        p.AreaW = 44
        p.AreaH = 26

        func box(_ x: Double, _ y: Double, _ w: Double, _ h: Double, _ material: Int) {
            p.Walls.append(WallItem(X1: x, Y1: y, X2: x + w, Y2: y, Material: material))
            p.Walls.append(WallItem(X1: x + w, Y1: y, X2: x + w, Y2: y + h, Material: material))
            p.Walls.append(WallItem(X1: x + w, Y1: y + h, X2: x, Y2: y + h, Material: material))
            p.Walls.append(WallItem(X1: x, Y1: y + h, X2: x, Y2: y, Material: material))
        }

        box(2, 2, 44, 26, 4)   // 외벽
        box(20, 11, 8, 8, 4)   // 코어
        box(2, 2, 12, 9, 0)    // 회의실
        box(14, 2, 10, 9, 1)   // 유리 회의실
        box(34, 2, 12, 12, 3)  // 서버실
        box(2, 19, 14, 9, 0)   // 사무실

        func model(_ name: String) -> ApModel { Catalog.all.first { $0.name == name }! }
        p.Aps.append(model("CW9166I").makeAp(name: "1F-Cisco-01", x: 12, y: 15))
        p.Aps.append(model("AP-635").makeAp(name: "1F-Aruba-01", x: 31, y: 8))
        p.Aps.append(model("AP45").makeAp(name: "1F-Mist-01", x: 33, y: 23))
        return p
    }
}
