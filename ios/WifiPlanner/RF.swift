import CoreGraphics
import Foundation

struct HeatResult {
    let cols: Int
    let rows: Int
    let cell: Double
    let values: [Float]
    let analyzed: Int
    let dead: Int
    let image: CGImage?
    let deadRuns: [CGRect]

    var covered: Int { analyzed - dead }
    var coveragePct: Double { analyzed == 0 ? 0 : 100.0 * Double(covered) / Double(analyzed) }
    var deadM2: Double { Double(dead) * cell * cell }
    var analyzedM2: Double { Double(analyzed) * cell * cell }
}

enum RF {
    static let freqMHz = [2437.0, 5500.0, 6500.0]
    static let bandNames = ["2.4 GHz", "5 GHz", "6 GHz"]
    static let clientHeight = 1.0
    static let idwRadius = 8.0
    static let noSignal = -100.0

    private static func cross(_ ax: Double, _ ay: Double, _ bx: Double, _ by: Double, _ cx: Double, _ cy: Double) -> Double {
        (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
    }

    static func crosses(_ ax: Double, _ ay: Double, _ bx: Double, _ by: Double, _ w: WallItem) -> Bool {
        let d1 = cross(w.X1, w.Y1, w.X2, w.Y2, ax, ay)
        let d2 = cross(w.X1, w.Y1, w.X2, w.Y2, bx, by)
        if abs(d2) < 1e-9 { return false } // 벽 위의 지점은 벽을 통과한 것으로 보지 않는다
        let d3 = cross(ax, ay, bx, by, w.X1, w.Y1)
        let d4 = cross(ax, ay, bx, by, w.X2, w.Y2)
        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0))
    }

    static func signal(_ p: Project, _ ap: ApItem, x: Double, y: Double, band: Int) -> Double {
        if band == 2 && !ap.Has6 { return -Double.infinity }
        let tx = band == 0 ? ap.Tx24 : band == 1 ? ap.Tx5 : ap.Tx6
        let gain = band == 0 ? ap.Gain24 : band == 1 ? ap.Gain5 : ap.Gain6
        let dx = ap.X - x
        let dy = ap.Y - y
        let dz = ap.Height - clientHeight
        let distance = max(1.0, (dx * dx + dy * dy + dz * dz).squareRoot())
        var loss = 20 * log10(freqMHz[band]) - 27.55 + 10 * p.Exponent * log10(distance)
        for wall in p.Walls where crosses(ap.X, ap.Y, x, y, wall) {
            loss += WallMaterial.all[wall.Material].loss[band]
        }
        return tx + gain - loss
    }

    static func best(_ p: Project, x: Double, y: Double, band: Int) -> (value: Double, server: ApItem?) {
        var value = -Double.infinity
        var server: ApItem? = nil
        for ap in p.Aps {
            let v = signal(p, ap, x: x, y: y, band: band)
            if v > value {
                value = v
                server = ap
            }
        }
        return (value, server)
    }

    static func pointValue(_ p: Project, _ point: SurveyPoint) -> Double {
        let values = point.Readings
            .filter { $0.Band == p.Band && (p.SurveySsid.isEmpty || $0.Ssid == p.SurveySsid) }
            .map { Double($0.Rssi) }
        return values.max() ?? noSignal
    }

    /// 측정 지점 사이를 거리 역가중으로 보간한다. 측정 반경 밖은 NaN.
    static func idw(_ points: [(x: Double, y: Double, value: Double)], x: Double, y: Double) -> Double {
        var sum = 0.0
        var weight = 0.0
        for point in points {
            let dx = point.x - x
            let dy = point.y - y
            let d2 = dx * dx + dy * dy
            if d2 > idwRadius * idwRadius { continue }
            if d2 < 1e-4 { return point.value }
            let w = 1.0 / d2
            sum += w * point.value
            weight += w
        }
        return weight == 0 ? Double.nan : sum / weight
    }

    /// mode 0: 예측, 1: 실측 보간
    static func compute(_ p: Project, mode: Int) -> HeatResult {
        let cell = max(0.05, max(p.PlanW, p.PlanH) / 300.0)
        let cols = max(1, Int((p.PlanW / cell).rounded(.up)))
        let rows = max(1, Int((p.PlanH / cell).rounded(.up)))
        let points = p.Points.map { (x: $0.X, y: $0.Y, value: pointValue(p, $0)) }
        var values = [Float](repeating: Float.nan, count: cols * rows)

        values.withUnsafeMutableBufferPointer { buffer in
            guard let base = buffer.baseAddress else { return }
            DispatchQueue.concurrentPerform(iterations: rows) { j in
                for i in 0..<cols {
                    let x = (Double(i) + 0.5) * cell
                    let y = (Double(j) + 0.5) * cell
                    if p.HasArea && (x < p.AreaX || x > p.AreaX + p.AreaW || y < p.AreaY || y > p.AreaY + p.AreaH) {
                        continue
                    }
                    if mode == 0 && !p.Aps.isEmpty {
                        base[j * cols + i] = Float(max(noSignal, best(p, x: x, y: y, band: p.Band).value))
                    } else if mode == 1 {
                        base[j * cols + i] = Float(idw(points, x: x, y: y))
                    }
                }
            }
        }
        return finish(values, cols: cols, rows: rows, cell: cell, threshold: p.Threshold)
    }

    private static func finish(_ values: [Float], cols: Int, rows: Int, cell: Double, threshold: Double) -> HeatResult {
        var analyzed = 0
        var dead = 0
        var runs: [CGRect] = []
        var pixels = [UInt8](repeating: 0, count: cols * rows * 4)
        for j in 0..<rows {
            var runStart = -1
            for i in 0...cols {
                var isDead = false
                if i < cols {
                    let v = values[j * cols + i]
                    if !v.isNaN {
                        analyzed += 1
                        isDead = Double(v) < threshold
                        if isDead { dead += 1 }
                        let c = color(Double(v), threshold: threshold)
                        let alpha = c.a / 255.0
                        let offset = (j * cols + i) * 4
                        pixels[offset] = UInt8(c.r * alpha)
                        pixels[offset + 1] = UInt8(c.g * alpha)
                        pixels[offset + 2] = UInt8(c.b * alpha)
                        pixels[offset + 3] = UInt8(c.a)
                    }
                }
                if isDead && runStart < 0 { runStart = i }
                if !isDead && runStart >= 0 {
                    runs.append(CGRect(x: Double(runStart) * cell, y: Double(j) * cell,
                                       width: Double(i - runStart) * cell, height: cell))
                    runStart = -1
                }
            }
        }
        return HeatResult(cols: cols, rows: rows, cell: cell, values: values, analyzed: analyzed, dead: dead,
                          image: makeImage(pixels, cols: cols, rows: rows), deadRuns: runs)
    }

    private static func makeImage(_ pixels: [UInt8], cols: Int, rows: Int) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(width: cols, height: rows, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: cols * 4,
                       space: CGColorSpaceCreateDeviceRGB(),
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }

    static func color(_ v: Double, threshold: Double) -> (r: Double, g: Double, b: Double, a: Double) {
        if v < threshold { return (190, 28, 48, 165) }
        let t = min(1, max(0, (v - threshold) / max(1, -45 - threshold)))
        let a = (245.0, 196.0, 40.0)
        let b = (132.0, 192.0, 58.0)
        let c = (12.0, 128.0, 84.0)
        let from = t < 0.5 ? a : b
        let to = t < 0.5 ? b : c
        let k = t < 0.5 ? t * 2 : (t - 0.5) * 2
        return (from.0 + (to.0 - from.0) * k, from.1 + (to.1 - from.1) * k, from.2 + (to.2 - from.2) * k, 145)
    }
}
