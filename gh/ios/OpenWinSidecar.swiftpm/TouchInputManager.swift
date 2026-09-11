import UIKit
import QuartzCore

public final class TouchInputManager: UIView {
    public var onTouchInput: ((String, Float, Float) -> Void)?
    public var onScroll: ((Int) -> Void)?
    public var onRightClick: (() -> Void)?

    private var lastMoveSentTime: CFTimeInterval = 0
    private var twoFingerStart: CGPoint?
    private var twoFingerStartTime: CFTimeInterval = 0

    override public init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        backgroundColor = .clear
    }

    required public init?(coder: NSCoder) {
        super.init(coder: coder)
        isMultipleTouchEnabled = true
        backgroundColor = .clear
    }

    override public func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let allTouches = event?.allTouches, allTouches.count == 2 {
            let pts = Array(allTouches).map { $0.location(in: self) }
            twoFingerStart = CGPoint(x: (pts[0].x + pts[1].x) / 2, y: (pts[0].y + pts[1].y) / 2)
            twoFingerStartTime = CACurrentMediaTime()
            return
        }

        guard let touch = touches.first else { return }
        let (normX, normY) = normalizedPoint(touch.location(in: self))
        onTouchInput?("down", normX, normY)
        lastMoveSentTime = CACurrentMediaTime()
    }

    override public func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        // Two-finger scroll
        if let allTouches = event?.allTouches, allTouches.count == 2, let start = twoFingerStart {
            let pts = Array(allTouches).map { $0.location(in: self) }
            let current = CGPoint(x: (pts[0].x + pts[1].x) / 2, y: (pts[0].y + pts[1].y) / 2)
            let deltaY = Int(round((start.y - current.y) * 2.5))
            if abs(deltaY) > 2 {
                onScroll?(deltaY)
                twoFingerStart = current
            }
            return
        }

        guard let touch = touches.first else { return }
        let now = CACurrentMediaTime()
        // Throttle move events to ~120Hz (8ms) to avoid saturating network
        if now - lastMoveSentTime >= 0.008 {
            lastMoveSentTime = now
            let (normX, normY) = normalizedPoint(touch.location(in: self))
            onTouchInput?("move", normX, normY)
        }
    }

    override public func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        // Two-finger tap = right click
        if let allTouches = event?.allTouches, allTouches.count == 2, twoFingerStart != nil {
            let elapsed = CACurrentMediaTime() - twoFingerStartTime
            if elapsed < 0.3 {
                onRightClick?()
            }
            twoFingerStart = nil
            return
        }
        twoFingerStart = nil

        guard let touch = touches.first else { return }
        let (normX, normY) = normalizedPoint(touch.location(in: self))
        onTouchInput?("up", normX, normY)
    }

    override public func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        twoFingerStart = nil
        guard let touch = touches.first else { return }
        let (normX, normY) = normalizedPoint(touch.location(in: self))
        onTouchInput?("up", normX, normY)
    }

    private func normalizedPoint(_ point: CGPoint) -> (Float, Float) {
        guard bounds.width > 0, bounds.height > 0 else { return (0, 0) }
        let x = Float(max(0.0, min(1.0, point.x / bounds.width)))
        let y = Float(max(0.0, min(1.0, point.y / bounds.height)))
        return (x, y)
    }
}
