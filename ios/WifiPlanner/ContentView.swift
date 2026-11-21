import PhotosUI
import SwiftUI
import UIKit
import UniformTypeIdentifiers

private enum ActiveSheet: String, Identifiable {
    case catalog, settings, apEditor, survey

    var id: String { rawValue }
}

private struct ShareFile: Identifiable {
    let id = UUID()
    let url: URL
}

private enum ImportKind {
    case project, planFile
}

struct ContentView: View {
    @StateObject private var store = PlanStore()
    @State private var sheet: ActiveSheet? = nil
    @State private var shareFile: ShareFile? = nil
    @State private var showImporter = false
    @State private var importKind = ImportKind.project
    @State private var showPhotos = false
    @State private var photoItem: PhotosPickerItem? = nil
    @State private var errorText: String? = nil
    @State private var scaleText = ""
    @State private var surveyLocation: (x: Double, y: Double)? = nil

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                StatsBar(store: store)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                PlanCanvasView(store: store)
                    .overlay(alignment: .bottomLeading) {
                        if store.project.View != 2 {
                            LegendView(threshold: store.project.Threshold).padding(8).allowsHitTesting(false)
                        }
                    }
                if !store.message.isEmpty {
                    Text(store.message)
                        .font(.footnote)
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 5)
                }
                ToolBarView(store: store) { sheet = .catalog }
            }
            .navigationTitle("Wi-Fi 음영 분석")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { toolbarContent }
        }
        .sheet(item: $sheet) { item in
            switch item {
            case .catalog: CatalogSheet(store: store)
            case .settings: SettingsSheet(store: store)
            case .apEditor: ApEditorSheet(store: store)
            case .survey:
                SurveySheet(defaultSsid: store.project.SurveySsid, defaultBand: store.project.Band) { ssid, band, rssi in
                    if let location = surveyLocation {
                        store.addSurvey(x: location.x, y: location.y, ssid: ssid, band: band, rssi: rssi)
                    }
                }
            }
        }
        .sheet(item: $shareFile) { file in
            ShareSheet(url: file.url)
        }
        .fileImporter(isPresented: $showImporter, allowedContentTypes: importKind == .project ? [.item] : [.image]) { result in
            handleImport(result)
        }
        .photosPicker(isPresented: $showPhotos, selection: $photoItem, matching: .images)
        .onChange(of: photoItem) { _, item in
            guard let item else { return }
            Task {
                do {
                    guard let data = try await item.loadTransferable(type: Data.self) else { throw PlanFileError.badImage }
                    try store.loadPlan(data: data)
                } catch {
                    errorText = error.localizedDescription
                }
                photoItem = nil
            }
        }
        .onChange(of: store.selection) { _, selection in
            if case .ap? = selection, store.tool == .select { sheet = .apEditor }
        }
        .onChange(of: store.pendingSurvey?.x) { _, _ in
            if let location = store.pendingSurvey {
                surveyLocation = location
                store.pendingSurvey = nil
                sheet = .survey
            }
        }
        .alert("축척 설정", isPresented: scaleAlertBinding) {
            TextField("실제 거리 (m)", text: $scaleText).keyboardType(.decimalPad)
            Button("확인") {
                let text = scaleText.replacingOccurrences(of: ",", with: ".")
                if let measured = store.pendingScale, let real = Double(text), real > 0 {
                    store.applyScale(measured: measured, real: real)
                }
                store.pendingScale = nil
            }
            Button("취소", role: .cancel) { store.pendingScale = nil }
        } message: {
            Text("두 점 사이의 실제 거리를 입력해 주세요.")
        }
        .alert("오류", isPresented: errorAlertBinding) {
            Button("확인", role: .cancel) { errorText = nil }
        } message: {
            Text(errorText ?? "")
        }
    }

    private var scaleAlertBinding: Binding<Bool> {
        Binding(get: { store.pendingScale != nil }, set: { if !$0 { store.pendingScale = nil } })
    }

    private var errorAlertBinding: Binding<Bool> {
        Binding(get: { errorText != nil }, set: { if !$0 { errorText = nil } })
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarLeading) {
            Menu {
                Button("사진에서 도면 불러오기") { showPhotos = true }
                Button("파일에서 도면 불러오기") {
                    importKind = .planFile
                    showImporter = true
                }
                Button("프로젝트 열기") {
                    importKind = .project
                    showImporter = true
                }
                Divider()
                Button("프로젝트 내보내기") { share("음영분석.wifiplan") { try store.projectData() } }
                Button("결과 이미지 내보내기") { share("음영분석결과.png") { try renderPNG() } }
                Menu("분석 리포트 내보내기") {
                    Button("HTML 리포트") { share("wifi-report.html") { try renderReport() } }
                    Button("AP 목록 CSV") { share("access-points.csv") { store.accessPointCSV() } }
                }
                Button("실측 CSV 내보내기") { share("실측데이터.csv") { store.surveyCSV() } }
                Divider()
                Button("예제 도면 열기") { store.newProject(DemoProject.make()) }
                Button("새 프로젝트") { store.newProject(Project()) }
            } label: {
                Image(systemName: "folder")
            }
            .accessibilityLabel("파일")
        }
        ToolbarItemGroup(placement: .topBarTrailing) {
            Button { store.undo() } label: { Image(systemName: "arrow.uturn.backward") }
                .disabled(!store.canUndo)
                .accessibilityLabel("실행 취소")
            Button { store.deleteSelection() } label: { Image(systemName: "trash") }
                .disabled(store.selection == nil)
                .accessibilityLabel("선택 항목 삭제")
            Button { sheet = .settings } label: { Image(systemName: "slider.horizontal.3") }
                .accessibilityLabel("표시 설정")
        }
    }

    private func handleImport(_ result: Result<URL, Error>) {
        do {
            let url = try result.get()
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            let data = try Data(contentsOf: url)
            if importKind == .project {
                try store.loadProject(data: data)
            } else {
                try store.loadPlan(data: data)
            }
        } catch {
            errorText = error.localizedDescription
        }
    }

    private func share(_ name: String, _ makeData: () throws -> Data) {
        do {
            let url = FileManager.default.temporaryDirectory.appendingPathComponent(name)
            try makeData().write(to: url, options: .atomic)
            shareFile = ShareFile(url: url)
        } catch {
            errorText = error.localizedDescription
        }
    }

    private func renderReport() throws -> Data {
        let image = try renderPNG().base64EncodedString()
        return store.reportHTML(coverageImage: image)
    }

    private func renderPNG() throws -> Data {
        store.computeNow()
        let renderer = ImageRenderer(content: ExportView(project: store.project, plan: store.planImage, heat: store.heat, width: 1600))
        renderer.scale = 1
        guard let data = renderer.uiImage?.pngData() else { throw PlanFileError.badImage }
        return data
    }
}

private struct ShareSheet: UIViewControllerRepresentable {
    let url: URL

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: [url], applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}

private struct StatsBar: View {
    @ObservedObject var store: PlanStore

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            if let heat = store.heat, heat.analyzed > 0 {
                Text(String(format: "커버리지 %.1f%%", heat.coveragePct))
                    .font(.headline)
                    .foregroundColor(heat.coveragePct >= 95 ? PlanColors.good : heat.coveragePct >= 80 ? .orange : PlanColors.dead)
                Spacer()
                Text(String(format: "음영 %.1f m²", heat.deadM2)).font(.subheadline)
            } else {
                Text(store.project.View == 1 ? "실측 지점을 추가해 주세요" : store.project.View == 0 ? "AP를 배치해 주세요" : "히트맵 꺼짐")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
                Spacer()
            }
            Text("\(RF.bandNames[store.project.Band]) · \(store.project.View == 1 ? "실측" : "예측")")
                .font(.caption)
                .foregroundColor(.secondary)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }
}

private struct ToolBarView: View {
    @ObservedObject var store: PlanStore
    let openCatalog: () -> Void

    var body: some View {
        HStack(spacing: 0) {
            ForEach(PlanTool.allCases) { tool in
                Button {
                    store.setTool(tool)
                    if tool == .placeAp { openCatalog() }
                } label: {
                    VStack(spacing: 3) {
                        Image(systemName: tool.icon).font(.system(size: 19))
                        Text(tool.title).font(.caption2)
                    }
                    .frame(maxWidth: .infinity, minHeight: 50)
                    .contentShape(Rectangle())
                    .foregroundColor(store.tool == tool ? PlanColors.accent : .primary)
                    .background(store.tool == tool ? PlanColors.accent.opacity(0.12) : Color.clear)
                }
                .accessibilityAddTraits(store.tool == tool ? .isSelected : [])
            }
        }
        .background(.regularMaterial)
    }
}

struct PlanCanvasView: View {
    @ObservedObject var store: PlanStore
    @State private var dragStart: CGPoint? = nil
    @State private var draggingAp: UUID? = nil
    @State private var panStart: CGSize = .zero
    @State private var zoomStart: CGFloat = 1
    @State private var moved = false

    var body: some View {
        GeometryReader { geometry in
            let size = geometry.size
            ZStack {
                Color(.systemGray5)
                PlanScene(project: store.project, plan: store.planImage, heat: store.heat, selection: store.selection) { _ in
                    store.transform(size: size)
                }
                drawingGuides(size: size)
            }
            .contentShape(Rectangle())
            .gesture(dragGesture(size: size))
            .simultaneousGesture(zoomGesture)
        }
        .clipped()
    }

    @ViewBuilder
    private func drawingGuides(size: CGSize) -> some View {
        let t = store.transform(size: size)
        if let first = store.firstPoint {
            Circle()
                .fill(PlanColors.accent)
                .frame(width: 12, height: 12)
                .position(t.screen(first.x, first.y))
                .allowsHitTesting(false)
        }
        if let area = store.dragArea {
            Rectangle()
                .stroke(PlanColors.accent, style: StrokeStyle(lineWidth: 2, dash: [8, 5]))
                .frame(width: area.width, height: area.height)
                .position(x: area.midX, y: area.midY)
                .allowsHitTesting(false)
        }
    }

    private func dragGesture(size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .local)
            .onChanged { value in
                let t = store.transform(size: size)
                if dragStart == nil {
                    dragStart = value.startLocation
                    panStart = store.pan
                    moved = false
                    if store.tool == .select, case .ap(let id)? = store.hitTest(value.startLocation, t) {
                        draggingAp = id
                        store.snapshot()
                    }
                }
                if hypot(value.translation.width, value.translation.height) < 8 && !moved { return }
                moved = true
                if let id = draggingAp {
                    store.moveAp(id: id, to: value.location, t)
                } else if store.tool == .area {
                    store.dragArea = CGRect(x: min(value.startLocation.x, value.location.x),
                                            y: min(value.startLocation.y, value.location.y),
                                            width: abs(value.translation.width), height: abs(value.translation.height))
                } else {
                    store.pan = CGSize(width: panStart.width + value.translation.width,
                                       height: panStart.height + value.translation.height)
                }
            }
            .onEnded { value in
                let t = store.transform(size: size)
                if !moved {
                    store.tap(at: value.location, t)
                } else if store.tool == .area && draggingAp == nil {
                    store.finishArea(from: value.startLocation, to: value.location, t)
                }
                dragStart = nil
                draggingAp = nil
                moved = false
            }
    }

    private var zoomGesture: some Gesture {
        MagnifyGesture()
            .onChanged { value in
                store.zoom = min(12, max(0.5, zoomStart * value.magnification))
            }
            .onEnded { _ in
                zoomStart = store.zoom
            }
    }
}

private struct CatalogSheet: View {
    @ObservedObject var store: PlanStore
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                ForEach(Catalog.vendors, id: \.self) { vendor in
                    Section(vendor) {
                        ForEach(Catalog.all.filter { $0.vendor == vendor }) { model in
                            Button {
                                store.model = model
                                dismiss()
                            } label: {
                                row(model)
                            }
                        }
                    }
                }
                Section("벽 재질") {
                    Picker("벽 재질", selection: $store.wallMaterial) {
                        ForEach(WallMaterial.all.indices, id: \.self) { index in
                            Text(WallMaterial.all[index].name).tag(index)
                        }
                    }
                }
            }
            .navigationTitle("AP 제품")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("닫기") { dismiss() } }
            }
        }
    }

    private func row(_ model: ApModel) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(model.name).foregroundColor(.primary)
                Text(gainText(model)).font(.caption).foregroundColor(.secondary)
                if !model.verified {
                    Text("참고값: 데이터시트 확인 필요").font(.caption2).foregroundColor(.orange)
                }
            }
            Spacer()
            if store.model == model {
                Image(systemName: "checkmark").foregroundColor(PlanColors.accent)
            }
        }
    }

    private func gainText(_ model: ApModel) -> String {
        let g6 = model.g6.map { String(format: "%g dBi", $0) } ?? "6GHz 없음"
        return String(format: "%@ · %g / %g / ", model.gen, model.g24, model.g5) + g6
    }
}

private struct SettingsSheet: View {
    @ObservedObject var store: PlanStore
    @Environment(\.dismiss) private var dismiss

    private let environments: [(name: String, value: Double)] = [
        ("개방형 공간", 2.2), ("일반 사무실", 2.6), ("밀집 공간", 3.0), ("창고 / 공장", 3.4),
    ]

    private func binding<T>(_ path: WritableKeyPath<Project, T>) -> Binding<T> {
        Binding(get: { store.project[keyPath: path] }, set: {
            store.project[keyPath: path] = $0
            store.recompute()
        })
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("표시") {
                    Picker("보기", selection: binding(\.View)) {
                        Text("예측 신호").tag(0)
                        Text("실측 신호").tag(1)
                        Text("히트맵 끄기").tag(2)
                    }
                    Picker("주파수 대역", selection: binding(\.Band)) {
                        ForEach(RF.bandNames.indices, id: \.self) { Text(RF.bandNames[$0]).tag($0) }
                    }
                    Stepper("음영 기준 \(Int(store.project.Threshold)) dBm", value: binding(\.Threshold), in: -90 ... -50, step: 1)
                    Picker("공간 유형", selection: binding(\.Exponent)) {
                        ForEach(environments, id: \.value) { Text($0.name).tag($0.value) }
                    }
                }
                Section("실측") {
                    Picker("SSID", selection: binding(\.SurveySsid)) {
                        Text("모든 SSID").tag("")
                        ForEach(store.surveySsids, id: \.self) { Text($0).tag($0) }
                    }
                    if let error = store.predictionError {
                        LabeledContent("예측과 실측 차이", value: String(format: "평균 %.1f dB", error))
                    }
                    Button("실측 지점 모두 지우기", role: .destructive) { store.clearPoints() }
                        .disabled(store.project.Points.isEmpty)
                }
                Section("범위") {
                    Button("분석 범위 해제") { store.clearArea() }
                        .disabled(!store.project.HasArea)
                }
                Section {
                    Text("예측 신호는 실제 건물에서 보정하지 않은 추정치입니다. AP 출력 기본값과 벽 손실도 설계용 가정값입니다.")
                        .font(.footnote)
                        .foregroundColor(.secondary)
                }
            }
            .navigationTitle("표시 설정")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
        }
    }
}

private struct ApEditorSheet: View {
    @ObservedObject var store: PlanStore
    @Environment(\.dismiss) private var dismiss

    private func binding<T>(_ path: WritableKeyPath<ApItem, T>, fallback: T) -> Binding<T> {
        Binding(get: { store.selectedAp?[keyPath: path] ?? fallback }, set: { value in
            store.updateSelectedAp { $0[keyPath: path] = value }
        })
    }

    var body: some View {
        NavigationStack {
            Form {
                if let ap = store.selectedAp {
                    Section("\(ap.Vendor) \(ap.Model)") {
                        TextField("이름", text: binding(\.Name, fallback: ""))
                        Stepper(String(format: "설치 높이 %.1f m", ap.Height), value: binding(\.Height, fallback: 3), in: 1...15, step: 0.5)
                    }
                    Section("송신 출력") {
                        Stepper("2.4 GHz  \(Int(ap.Tx24)) dBm", value: binding(\.Tx24, fallback: 15), in: 0...30, step: 1)
                        Stepper("5 GHz  \(Int(ap.Tx5)) dBm", value: binding(\.Tx5, fallback: 18), in: 0...30, step: 1)
                        if ap.Has6 {
                            Stepper("6 GHz  \(Int(ap.Tx6)) dBm", value: binding(\.Tx6, fallback: 18), in: 0...30, step: 1)
                        }
                    }
                    Section {
                        Button("AP 삭제", role: .destructive) {
                            store.deleteSelection()
                            dismiss()
                        }
                    }
                }
            }
            .navigationTitle("선택한 AP")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
        }
        .presentationDetents([.medium, .large])
    }
}

private struct SurveySheet: View {
    let onSave: (String, Int, Int) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var ssid: String
    @State private var band: Int
    @State private var rssi = -60

    init(defaultSsid: String, defaultBand: Int, onSave: @escaping (String, Int, Int) -> Void) {
        self.onSave = onSave
        _ssid = State(initialValue: defaultSsid)
        _band = State(initialValue: defaultBand)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("SSID", text: $ssid)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    Picker("주파수 대역", selection: $band) {
                        ForEach(RF.bandNames.indices, id: \.self) { Text(RF.bandNames[$0]).tag($0) }
                    }
                    Stepper("신호 \(rssi) dBm", value: $rssi, in: -100 ... -20)
                } footer: {
                    Text("iOS는 앱이 Wi-Fi 신호 세기를 읽는 것을 허용하지 않습니다. 측정기나 다른 기기에서 확인한 값을 입력해 주세요.")
                }
            }
            .navigationTitle("실측 값 입력")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("취소") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("추가") {
                        onSave(ssid, band, rssi)
                        dismiss()
                    }
                }
            }
        }
        .presentationDetents([.medium])
    }
}
