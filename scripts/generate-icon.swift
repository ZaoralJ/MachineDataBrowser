import Cocoa
import CoreGraphics

func createIcon(size: CGFloat) -> NSImage {
    let img = NSImage(size: NSSize(width: size, height: size))
    img.lockFocus()
    guard let ctx = NSGraphicsContext.current?.cgContext else {
        img.unlockFocus()
        return img
    }

    ctx.saveGState()

    let scale = size / 1024.0
    let squircleRect = CGRect(x: 100 * scale, y: 100 * scale, width: 824 * scale, height: 824 * scale)
    let cornerRadius: CGFloat = 185 * scale

    let squirclePath = CGPath(roundedRect: squircleRect,
                               cornerWidth: cornerRadius,
                               cornerHeight: cornerRadius,
                               transform: nil)

    ctx.saveGState()
    let shadowColor = CGColor(red: 0, green: 0, blue: 0, alpha: 0.42)
    ctx.setShadow(offset: CGSize(width: 0, height: -24 * scale), blur: 38 * scale, color: shadowColor)
    ctx.addPath(squirclePath)
    ctx.setFillColor(CGColor(red: 0.04, green: 0.06, blue: 0.1, alpha: 1.0))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(squirclePath)
    ctx.clip()

    let colorSpace = CGColorSpaceCreateDeviceRGB()
    let bgColors = [
        CGColor(red: 0.12, green: 0.16, blue: 0.26, alpha: 1.0),
        CGColor(red: 0.07, green: 0.09, blue: 0.16, alpha: 1.0),
        CGColor(red: 0.03, green: 0.04, blue: 0.09, alpha: 1.0)
    ] as CFArray
    let bgLocations: [CGFloat] = [0.0, 0.55, 1.0]
    if let bgGradient = CGGradient(colorsSpace: colorSpace, colors: bgColors, locations: bgLocations) {
        ctx.drawLinearGradient(bgGradient,
                               start: CGPoint(x: 512 * scale, y: 924 * scale),
                               end: CGPoint(x: 512 * scale, y: 100 * scale),
                               options: [])
    }

    let glowColors = [
        CGColor(red: 0.05, green: 0.5, blue: 0.95, alpha: 0.28),
        CGColor(red: 0.0, green: 0.75, blue: 0.8, alpha: 0.1),
        CGColor(red: 0.0, green: 0.0, blue: 0.0, alpha: 0.0)
    ] as CFArray
    if let glowGradient = CGGradient(colorsSpace: colorSpace, colors: glowColors, locations: [0.0, 0.55, 1.0]) {
        ctx.drawRadialGradient(glowGradient,
                               startCenter: CGPoint(x: 512 * scale, y: 560 * scale),
                               startRadius: 30 * scale,
                               endCenter: CGPoint(x: 512 * scale, y: 560 * scale),
                               endRadius: 420 * scale,
                               options: [])
    }

    ctx.saveGState()
    ctx.setStrokeColor(CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.04))
    ctx.setLineWidth(1.5 * scale)
    let gridSpacing: CGFloat = 82.4 * scale
    var x = squircleRect.minX + 41.2 * scale
    while x < squircleRect.maxX {
        ctx.move(to: CGPoint(x: x, y: squircleRect.minY))
        ctx.addLine(to: CGPoint(x: x, y: squircleRect.maxY))
        x += gridSpacing
    }
    var y = squircleRect.minY + 41.2 * scale
    while y < squircleRect.maxY {
        ctx.move(to: CGPoint(x: squircleRect.minX, y: y))
        ctx.addLine(to: CGPoint(x: squircleRect.maxX, y: y))
        y += gridSpacing
    }
    ctx.strokePath()
    ctx.restoreGState()

    ctx.saveGState()
    let centerPt = CGPoint(x: 512 * scale, y: 500 * scale)
    let ringRadii: [CGFloat] = [175 * scale, 290 * scale]
    for r in ringRadii {
        ctx.setStrokeColor(CGColor(red: 0.2, green: 0.6, blue: 0.95, alpha: 0.14))
        ctx.setLineWidth(2.0 * scale)
        ctx.setLineDash(phase: 0, lengths: [6 * scale, 9 * scale])
        ctx.strokeEllipse(in: CGRect(x: centerPt.x - r, y: centerPt.y - r, width: r * 2, height: r * 2))
    }
    ctx.restoreGState()

    let root = CGPoint(x: 512 * scale, y: 700 * scale)
    let leftNode = CGPoint(x: 290 * scale, y: 475 * scale)
    let rightNode = CGPoint(x: 734 * scale, y: 475 * scale)
    let bottomNode = CGPoint(x: 512 * scale, y: 275 * scale)

    let busLines = [
        (root, leftNode),
        (root, rightNode),
        (leftNode, bottomNode),
        (rightNode, bottomNode)
    ]

    ctx.saveGState()
    ctx.setShadow(offset: .zero, blur: 18 * scale, color: CGColor(red: 0.0, green: 0.7, blue: 1.0, alpha: 0.65))
    ctx.setStrokeColor(CGColor(red: 0.0, green: 0.55, blue: 0.95, alpha: 0.45))
    ctx.setLineWidth(12 * scale)
    ctx.setLineCap(.round)
    for (p1, p2) in busLines {
        ctx.move(to: p1)
        ctx.addLine(to: p2)
    }
    ctx.strokePath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.setStrokeColor(CGColor(red: 0.45, green: 0.88, blue: 1.0, alpha: 0.95))
    ctx.setLineWidth(4.5 * scale)
    ctx.setLineCap(.round)
    for (p1, p2) in busLines {
        ctx.move(to: p1)
        ctx.addLine(to: p2)
    }
    ctx.strokePath()
    ctx.restoreGState()

    let packetFractions: [CGFloat] = [0.35, 0.65]
    for (p1, p2) in busLines {
        for frac in packetFractions {
            let px = p1.x + (p2.x - p1.x) * frac
            let py = p1.y + (p2.y - p1.y) * frac
            ctx.saveGState()
            ctx.setShadow(offset: .zero, blur: 10 * scale, color: CGColor(red: 0.5, green: 0.9, blue: 1.0, alpha: 1.0))
            ctx.setFillColor(CGColor(red: 0.85, green: 0.98, blue: 1.0, alpha: 0.95))
            ctx.fillEllipse(in: CGRect(x: px - 5 * scale, y: py - 5 * scale, width: 10 * scale, height: 10 * scale))
            ctx.restoreGState()
        }
    }

    ctx.saveGState()
    ctx.setShadow(offset: .zero, blur: 16 * scale, color: CGColor(red: 0.05, green: 0.95, blue: 0.6, alpha: 0.85))
    let wavePath = CGMutablePath()
    wavePath.move(to: CGPoint(x: 215 * scale, y: 475 * scale))
    wavePath.addLine(to: CGPoint(x: 375 * scale, y: 475 * scale))
    wavePath.addLine(to: CGPoint(x: 420 * scale, y: 415 * scale))
    wavePath.addLine(to: CGPoint(x: 468 * scale, y: 555 * scale))
    wavePath.addLine(to: CGPoint(x: 512 * scale, y: 395 * scale))
    wavePath.addLine(to: CGPoint(x: 556 * scale, y: 535 * scale))
    wavePath.addLine(to: CGPoint(x: 604 * scale, y: 475 * scale))
    wavePath.addLine(to: CGPoint(x: 809 * scale, y: 475 * scale))

    ctx.addPath(wavePath)
    ctx.setStrokeColor(CGColor(red: 0.05, green: 0.85, blue: 0.55, alpha: 0.45))
    ctx.setLineWidth(9 * scale)
    ctx.setLineCap(.round)
    ctx.setLineJoin(.round)
    ctx.strokePath()

    ctx.addPath(wavePath)
    ctx.setStrokeColor(CGColor(red: 0.55, green: 1.0, blue: 0.78, alpha: 1.0))
    ctx.setLineWidth(4.0 * scale)
    ctx.strokePath()
    ctx.restoreGState()

    func drawNodePlate(center: CGPoint, radius: CGFloat, mainColor: (r: CGFloat, g: CGFloat, b: CGFloat), accentColor: (r: CGFloat, g: CGFloat, b: CGFloat)) {
        ctx.saveGState()
        ctx.setShadow(offset: .zero, blur: 28 * scale, color: CGColor(red: mainColor.r, green: mainColor.g, blue: mainColor.b, alpha: 0.8))
        let nodeRect = CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
        ctx.addEllipse(in: nodeRect)
        ctx.setFillColor(CGColor(red: 0.06, green: 0.09, blue: 0.16, alpha: 1.0))
        ctx.fillPath()
        ctx.restoreGState()

        ctx.saveGState()
        ctx.addEllipse(in: nodeRect)
        ctx.clip()

        let sphereColors = [
            CGColor(red: accentColor.r, green: accentColor.g, blue: accentColor.b, alpha: 1.0),
            CGColor(red: mainColor.r * 0.8, green: mainColor.g * 0.8, blue: mainColor.b * 0.8, alpha: 1.0),
            CGColor(red: mainColor.r * 0.25, green: mainColor.g * 0.25, blue: mainColor.b * 0.25, alpha: 1.0)
        ] as CFArray
        if let sphereGrad = CGGradient(colorsSpace: colorSpace, colors: sphereColors, locations: [0.0, 0.55, 1.0]) {
            ctx.drawRadialGradient(sphereGrad,
                                   startCenter: CGPoint(x: center.x - radius * 0.35, y: center.y + radius * 0.35),
                                   startRadius: 2 * scale,
                                   endCenter: center,
                                   endRadius: radius,
                                   options: [])
        }
        ctx.restoreGState()

        ctx.saveGState()
        ctx.setStrokeColor(CGColor(red: accentColor.r, green: accentColor.g, blue: accentColor.b, alpha: 0.9))
        ctx.setLineWidth(3.8 * scale)
        ctx.strokeEllipse(in: nodeRect)
        ctx.restoreGState()
    }

    drawNodePlate(center: root,
                  radius: 76 * scale,
                  mainColor: (0.05, 0.45, 0.95),
                  accentColor: (0.4, 0.85, 1.0))

    ctx.saveGState()
    let serverWidth: CGFloat = 68 * scale
    let serverHeight: CGFloat = 16 * scale
    let serverYPositions = [root.y + 16 * scale, root.y - 8 * scale, root.y - 32 * scale]
    for sy in serverYPositions {
        let bladeRect = CGRect(x: root.x - serverWidth / 2, y: sy, width: serverWidth, height: serverHeight)
        let bladePath = CGPath(roundedRect: bladeRect, cornerWidth: 3.5 * scale, cornerHeight: 3.5 * scale, transform: nil)
        ctx.addPath(bladePath)
        ctx.setFillColor(CGColor(red: 0.08, green: 0.16, blue: 0.32, alpha: 0.9))
        ctx.fillPath()
        ctx.addPath(bladePath)
        ctx.setStrokeColor(CGColor(red: 0.8, green: 0.95, blue: 1.0, alpha: 0.95))
        ctx.setLineWidth(2.2 * scale)
        ctx.strokePath()

        let ledRect = CGRect(x: root.x - serverWidth / 2 + 6 * scale, y: sy + 4.5 * scale, width: 7 * scale, height: 7 * scale)
        ctx.setFillColor(CGColor(red: 0.1, green: 1.0, blue: 0.6, alpha: 1.0))
        ctx.fillEllipse(in: ledRect)

        let slit1 = CGRect(x: root.x + 8 * scale, y: sy + 6 * scale, width: 14 * scale, height: 3.5 * scale)
        let slit2 = CGRect(x: root.x + 25 * scale, y: sy + 6 * scale, width: 6 * scale, height: 3.5 * scale)
        ctx.setFillColor(CGColor(red: 0.8, green: 0.92, blue: 1.0, alpha: 0.7))
        ctx.fill(slit1)
        ctx.fill(slit2)
    }
    ctx.restoreGState()

    drawNodePlate(center: leftNode,
                  radius: 58 * scale,
                  mainColor: (0.2, 0.38, 0.85),
                  accentColor: (0.5, 0.75, 1.0))

    ctx.saveGState()
    let folderW: CGFloat = 48 * scale
    let folderH: CGFloat = 34 * scale
    let fLeft = leftNode.x - folderW / 2
    let fBottom = leftNode.y - folderH / 2
    let folderPath = CGMutablePath()
    folderPath.move(to: CGPoint(x: fLeft, y: fBottom))
    folderPath.addLine(to: CGPoint(x: fLeft, y: fBottom + folderH - 7 * scale))
    folderPath.addLine(to: CGPoint(x: fLeft + 16 * scale, y: fBottom + folderH - 7 * scale))
    folderPath.addLine(to: CGPoint(x: fLeft + 22 * scale, y: fBottom + folderH))
    folderPath.addLine(to: CGPoint(x: fLeft + folderW, y: fBottom + folderH))
    folderPath.addLine(to: CGPoint(x: fLeft + folderW, y: fBottom))
    folderPath.closeSubpath()

    ctx.addPath(folderPath)
    ctx.setFillColor(CGColor(red: 0.08, green: 0.18, blue: 0.4, alpha: 0.85))
    ctx.fillPath()
    ctx.addPath(folderPath)
    ctx.setStrokeColor(CGColor(red: 0.8, green: 0.92, blue: 1.0, alpha: 1.0))
    ctx.setLineWidth(2.6 * scale)
    ctx.strokePath()

    ctx.move(to: CGPoint(x: fLeft + 9 * scale, y: fBottom + 12 * scale))
    ctx.addLine(to: CGPoint(x: fLeft + folderW - 9 * scale, y: fBottom + 12 * scale))
    ctx.move(to: CGPoint(x: fLeft + 9 * scale, y: fBottom + 20 * scale))
    ctx.addLine(to: CGPoint(x: fLeft + folderW - 14 * scale, y: fBottom + 20 * scale))
    ctx.strokePath()
    ctx.restoreGState()

    drawNodePlate(center: rightNode,
                  radius: 58 * scale,
                  mainColor: (0.05, 0.68, 0.45),
                  accentColor: (0.35, 0.98, 0.75))

    ctx.saveGState()
    let eyePath = CGMutablePath()
    let eyeLeft = CGPoint(x: rightNode.x - 30 * scale, y: rightNode.y)
    let eyeRight = CGPoint(x: rightNode.x + 30 * scale, y: rightNode.y)
    eyePath.move(to: eyeLeft)
    eyePath.addCurve(to: eyeRight,
                     control1: CGPoint(x: rightNode.x - 12 * scale, y: rightNode.y + 22 * scale),
                     control2: CGPoint(x: rightNode.x + 12 * scale, y: rightNode.y + 22 * scale))
    eyePath.addCurve(to: eyeLeft,
                     control1: CGPoint(x: rightNode.x + 12 * scale, y: rightNode.y - 22 * scale),
                     control2: CGPoint(x: rightNode.x - 12 * scale, y: rightNode.y - 22 * scale))
    eyePath.closeSubpath()

    ctx.addPath(eyePath)
    ctx.setFillColor(CGColor(red: 0.04, green: 0.22, blue: 0.16, alpha: 0.85))
    ctx.fillPath()
    ctx.addPath(eyePath)
    ctx.setStrokeColor(CGColor(red: 0.8, green: 1.0, blue: 0.9, alpha: 1.0))
    ctx.setLineWidth(2.6 * scale)
    ctx.strokePath()

    let pupilRadius: CGFloat = 10 * scale
    ctx.setFillColor(CGColor(red: 0.3, green: 1.0, blue: 0.7, alpha: 1.0))
    ctx.fillEllipse(in: CGRect(x: rightNode.x - pupilRadius, y: rightNode.y - pupilRadius, width: pupilRadius * 2, height: pupilRadius * 2))
    ctx.setFillColor(CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.95))
    ctx.fillEllipse(in: CGRect(x: rightNode.x - 3 * scale, y: rightNode.y + 2 * scale, width: 5 * scale, height: 5 * scale))
    ctx.restoreGState()

    drawNodePlate(center: bottomNode,
                  radius: 60 * scale,
                  mainColor: (0.85, 0.52, 0.08),
                  accentColor: (1.0, 0.82, 0.35))

    ctx.saveGState()
    let tagSize = 56 * scale
    let font = NSFont.monospacedSystemFont(ofSize: tagSize, weight: .bold)
    let attrs: [NSAttributedString.Key: Any] = [
        .font: font,
        .foregroundColor: NSColor(calibratedRed: 1.0, green: 0.96, blue: 0.88, alpha: 1.0)
    ]
    let str = NSAttributedString(string: "{ }", attributes: attrs)
    let strSize = str.size()
    let textRect = CGRect(x: bottomNode.x - strSize.width / 2,
                          y: bottomNode.y - strSize.height / 2,
                          width: strSize.width,
                          height: strSize.height)
    str.draw(in: textRect)
    ctx.restoreGState()

    ctx.saveGState()
    let centerHub = CGPoint(x: 512 * scale, y: 485 * scale)
    let hubRadius: CGFloat = 38 * scale
    ctx.setShadow(offset: .zero, blur: 20 * scale, color: CGColor(red: 0.0, green: 0.9, blue: 1.0, alpha: 0.9))
    ctx.setStrokeColor(CGColor(red: 0.85, green: 0.98, blue: 1.0, alpha: 1.0))
    ctx.setLineWidth(4.2 * scale)
    ctx.strokeEllipse(in: CGRect(x: centerHub.x - hubRadius, y: centerHub.y - hubRadius, width: hubRadius * 2, height: hubRadius * 2))

    ctx.setFillColor(CGColor(red: 0.0, green: 0.88, blue: 1.0, alpha: 0.95))
    ctx.fillEllipse(in: CGRect(x: centerHub.x - 11 * scale, y: centerHub.y - 11 * scale, width: 22 * scale, height: 22 * scale))
    ctx.setFillColor(CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 1.0))
    ctx.fillEllipse(in: CGRect(x: centerHub.x - 4 * scale, y: centerHub.y + 2 * scale, width: 8 * scale, height: 8 * scale))
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(squirclePath)
    ctx.setLineWidth(3.0 * scale)
    ctx.setStrokeColor(CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.2))
    ctx.strokePath()

    let glintRect = CGRect(x: 102 * scale, y: 920 * scale, width: 820 * scale, height: 4 * scale)
    let glintPath = CGPath(roundedRect: glintRect, cornerWidth: 2 * scale, cornerHeight: 2 * scale, transform: nil)
    ctx.addPath(glintPath)
    ctx.setFillColor(CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.14))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.restoreGState()
    ctx.restoreGState()

    img.unlockFocus()
    return img
}

let icon = createIcon(size: 1024)
if let tiff = icon.tiffRepresentation,
   let rep = NSBitmapImageRep(data: tiff),
   let pngData = rep.representation(using: .png, properties: [:]) {
    let outUrl = URL(fileURLWithPath: "packaging/macos/AppIcon-1024.png")
    try pngData.write(to: outUrl)
    print("Generated 1024x1024 icon at \(outUrl.path)")
} else {
    print("Failed to generate icon PNG")
    exit(1)
}
