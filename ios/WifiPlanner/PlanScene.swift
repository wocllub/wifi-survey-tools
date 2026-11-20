import SwiftUI
import UIKit

private func rgb(_ r: Double, _ g: Double, _ b: Double) -> Color {
    Color(red: r / 255, green: g / 255, blue: b / 255)
}

enum PlanColors {
    static let accent = rgb(0, 103, 192)
    static let dead = rgb(190, 28, 48)
    static let good = rgb(12, 118, 78)

    static func vendor(_ name: String) -> Color {
        switch name {
        case "Cisco": return rgb(0, 92, 170)
        case "HPE Aruba": return rgb(214, 96, 16)
        case "Juniper Mist": return rgb(0, 128, 120)
        case "Grandstream": return rgb(120, 70, 180)
        case "H3C": return rgb(150, 110, 0)
        default: return rgb(90, 96, 108)
        }
    }

    static func material(_ index: Int) -> Color {
        switch index {
        case 0: return rgb(120, 130, 154)
        case 1: return rgb(40, 150, 200)
        case 2: return rgb(150, 100, 50)
        case 3: return rgb(170, 80, 50)
        case 4: return rgb(50, 55, 65)
        default: return rgb(120, 40, 140)
        }
    }
}

/// 도면, 히트맵, 벽, 실측 지점, AP를 그린다. 화면과 이미지 내보내기에서 함께 쓴다.
struct PlanScene: View {
    let project: Project
    let plan: UIImage?
    let heat: HeatResult?
    let selection: PlanSelection?
    let transform: (CGSize) -> PlanTransform

    var body: some View {
        Canvas { context, size in
            draw(&context, size: size, t: transform(size))
        }
    }

    private func label(_ context: inout GraphicsContext, _ text: String, color: Color, center: CGPoint, top: CGFloat) {
        let resolved = context.resolve(Text(text).font(.system(size: 11, weight: .semibold)).foregroundColor(color))
        let size = resolved.measure(in: CGSize(width: 240, height: 40))
        let box = CGRect(x: center.x - size.width / 2 - 3, y: top, width: size.width + 6, height: size.height + 2)
        context.fill(Path(box), with: .color(.white.opacity(0.88)))
        context.draw(resolved, at: CGPoint(x: center.x, y: top + 1), anchor: .top)
    }

    private func draw(_ context: inout GraphicsContext, size: CGSize, t: PlanTransform) {
        let origin = t.screen(0, 0)
        let rect = CGRect(x: origin.x, y: origin.y, width: CGFloat(project.PlanW) * t.scale, height: CGFloat(project.PlanH) * t.scale)
        context.fill(Path(rect), with: .color(.white))
        if let plan {
            context.draw(Image(uiImage: plan), in: rect)
        } else {
            drawGrid(&context, rect: rect, t: t)
        }
        if project.View != 2, let heat {
            drawHeat(&context, heat: heat, rect: rect, t: t)
        }
        context.stroke(Path(rect), with: .color(.gray.opacity(0.7)), lineWidth: 1)

        if project.HasArea {
            let a = t.screen(project.AreaX, project.AreaY)
            let area = CGRect(x: a.x, y: a.y, width: CGFloat(project.AreaW) * t.scale, height: CGFloat(project.AreaH) * t.scale)
            context.stroke(Path(area), with: .color(PlanColors.accent), style: StrokeStyle(lineWidth: 2, dash: [8, 5]))
        }

        for wall in project.Walls {
            var path = Path()
            path.move(to: t.screen(wall.X1, wall.Y1))
            path.addLine(to: t.screen(wall.X2, wall.Y2))
            let selected = selection == .wall(wall.id)
            context.stroke(path, with: .color(selected ? PlanColors.accent : PlanColors.material(wall.Material)),
                           style: StrokeStyle(lineWidth: selected ? 5 : 3, lineCap: .round))
        }

        for point in project.Points {
            let c = t.screen(point.X, point.Y)
            let value = RF.pointValue(project, point)
            let color = value < project.Threshold ? PlanColors.dead : PlanColors.good
            let selected = selection == .point(point.id)
            let r: CGFloat = selected ? 8 : 6
            let dot = Path(ellipseIn: CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2))
            context.fill(dot, with: .color(color))
            context.stroke(dot, with: .color(selected ? PlanColors.accent : .white), lineWidth: 2)
            label(&context, value <= RF.noSignal ? "신호 없음" : String(format: "%.0f", value), color: color, center: c, top: c.y + r + 1)
        }

        for ap in project.Aps {
            let c = t.screen(ap.X, ap.Y)
            let r: CGFloat = 15
            if selection == .ap(ap.id) {
                let ring = Path(ellipseIn: CGRect(x: c.x - r - 4, y: c.y - r - 4, width: (r + 4) * 2, height: (r + 4) * 2))
                context.stroke(ring, with: .color(PlanColors.accent), lineWidth: 3)
            }
            let circle = Path(ellipseIn: CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2))
            context.fill(circle, with: .color(PlanColors.vendor(ap.Vendor)))
            context.stroke(circle, with: .color(.white), lineWidth: 2)
            context.draw(Text(Catalog.mark(for: ap.Vendor)).font(.system(size: 12, weight: .bold)).foregroundColor(.white), at: c)
            label(&context, ap.Name, color: .black, center: c, top: c.y + r + 3)
        }
    }

    private func drawGrid(_ context: inout GraphicsContext, rect: CGRect, t: PlanTransform) {
        let step = t.scale >= 12 ? 1.0 : 5.0
        var path = Path()
        var x = 0.0
        while x <= project.PlanW {
            path.move(to: t.screen(x, 0))
            path.addLine(to: t.screen(x, project.PlanH))
            x += step
        }
        var y = 0.0
        while y <= project.PlanH {
            path.move(to: t.screen(0, y))
            path.addLine(to: t.screen(project.PlanW, y))
            y += step
        }
        context.stroke(path, with: .color(.gray.opacity(0.18)), lineWidth: 0.5)
    }

    private func drawHeat(_ context: inout GraphicsContext, heat: HeatResult, rect: CGRect, t: PlanTransform) {
        var layer = context
        layer.clip(to: Path(rect))
        if let image = heat.image {
            let w = CGFloat(Double(heat.cols) * heat.cell) * t.scale
            let h = CGFloat(Double(heat.rows) * heat.cell) * t.scale
            layer.draw(Image(decorative: image, scale: 1).interpolation(.medium), in: CGRect(x: rect.minX, y: rect.minY, width: w, height: h))
        }
        guard !heat.deadRuns.isEmpty else { return }
        var deadPath = Path()
        for run in heat.deadRuns {
            let a = t.screen(Double(run.minX), Double(run.minY))
            deadPath.addRect(CGRect(x: a.x, y: a.y, width: run.width * t.scale + 0.5, height: run.height * t.scale + 0.5))
        }
        layer.clip(to: deadPath)
        var hatch = Path()
        var offset = -rect.height
        while offset < rect.width {
            hatch.move(to: CGPoint(x: rect.minX + offset, y: rect.maxY))
            hatch.addLine(to: CGPoint(x: rect.minX + offset + rect.height, y: rect.minY))
            offset += 9
        }
        layer.stroke(hatch, with: .color(.white.opacity(0.5)), lineWidth: 1.5)
    }
}

struct LegendView: View {
    let threshold: Double

    var body: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Rectangle().fill(PlanColors.dead).frame(width: 54, height: 10)
                Text("\(Int(threshold)) 미만 음영").font(.system(size: 10))
            }
            VStack(alignment: .leading, spacing: 2) {
                LinearGradient(colors: [rgb(245, 196, 40), rgb(132, 192, 58), rgb(12, 128, 84)],
                               startPoint: .leading, endPoint: .trailing)
                    .frame(width: 130, height: 10)
                HStack {
                    Text("\(Int(threshold))")
                    Spacer()
                    Text("-45 dBm")
                }
                .font(.system(size: 10))
                .frame(width: 130)
            }
        }
        .padding(8)
        .foregroundColor(.black)
        .background(Color.white.opacity(0.92))
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }
}

struct ExportView: View {
    let project: Project
    let plan: UIImage?
    let heat: HeatResult?
    let width: CGFloat

    private var planHeight: CGFloat { width * CGFloat(project.PlanH / project.PlanW) }

    private var summary: String {
        guard let heat, heat.analyzed > 0 else { return "분석 데이터 없음" }
        return String(format: "커버리지 %.1f%%  ·  음영 %.1f m²  ·  기준 %.0f dBm  ·  AP %d대  ·  실측 %d곳",
                      heat.coveragePct, heat.deadM2, project.Threshold, project.Aps.count, project.Points.count)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Wi-Fi 음영 분석 (\(project.View == 1 ? "실측" : "예측") · \(RF.bandNames[project.Band]))")
                        .font(.system(size: 24, weight: .bold))
                    Text(summary).font(.system(size: 15)).foregroundColor(.gray)
                }
                Spacer()
                LegendView(threshold: project.Threshold)
            }
            PlanScene(project: project, plan: plan, heat: heat, selection: nil) { size in
                let scale = size.width / CGFloat(project.PlanW)
                return PlanTransform(scale: scale, originX: 0, originY: 0)
            }
            .frame(width: width, height: planHeight)
        }
        .padding(16)
        .frame(width: width + 32)
        .foregroundColor(.black)
        .background(Color.white)
        .environment(\.colorScheme, .light)
    }
}
