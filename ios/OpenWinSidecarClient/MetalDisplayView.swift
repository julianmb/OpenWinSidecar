import UIKit
import Metal
import MetalKit
import QuartzCore
import CoreVideo

public final class MetalDisplayView: UIView {
    private var metalLayer: CAMetalLayer { layer as! CAMetalLayer }
    private var device: MTLDevice!
    private var commandQueue: MTLCommandQueue!
    private var pipelineState: MTLRenderPipelineState!
    private var textureSampler: MTLSamplerState!
    private var textureCache: CVMetalTextureCache?

    private var currentPixelBuffer: CVPixelBuffer?
    private let renderLock = NSLock()
    private var displayLink: CADisplayLink?

    public var onFpsUpdate: ((Int) -> Void)?
    private var frameCounter = 0
    private var lastFpsTimestamp: CFTimeInterval = 0

    override public class var layerClass: AnyClass {
        return CAMetalLayer.self
    }

    override public init(frame: CGRect) {
        super.init(frame: frame)
        setupMetal()
    }

    required public init?(coder: NSCoder) {
        super.init(coder: coder)
        setupMetal()
    }

    private func setupMetal() {
        guard let mtlDevice = MTLCreateSystemDefaultDevice() else {
            print("[Metal] Metal is not supported on this device")
            return
        }
        self.device = mtlDevice
        self.commandQueue = mtlDevice.makeCommandQueue()

        metalLayer.device = mtlDevice
        metalLayer.pixelFormat = .bgra8Unorm
        metalLayer.framebufferOnly = true
        metalLayer.presentsWithTransaction = false
        metalLayer.drawsAsynchronously = true

        CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, mtlDevice, nil, &textureCache)

        setupPipeline()
        setupDisplayLink()
    }

    private func setupPipeline() {
        guard let library = try? device.makeLibrary(source: shadersSource, options: nil) else {
            print("[Metal] Failed to compile shaders from source")
            return
        }

        guard let vertexFunction = library.makeFunction(name: "vertexShader"),
              let fragmentFunction = library.makeFunction(name: "fragmentShaderNV12") else {
            print("[Metal] Failed to find shader functions")
            return
        }

        let pipelineDescriptor = MTLRenderPipelineDescriptor()
        pipelineDescriptor.vertexFunction = vertexFunction
        pipelineDescriptor.fragmentFunction = fragmentFunction
        pipelineDescriptor.colorAttachments[0].pixelFormat = .bgra8Unorm

        do {
            pipelineState = try device.makeRenderPipelineState(descriptor: pipelineDescriptor)
        } catch {
            print("[Metal] Failed to create pipeline state: \(error)")
        }

        let samplerDesc = MTLSamplerDescriptor()
        samplerDesc.minFilter = .linear
        samplerDesc.magFilter = .linear
        samplerDesc.sAddressMode = .clampToEdge
        samplerDesc.tAddressMode = .clampToEdge
        textureSampler = device.makeSamplerState(descriptor: samplerDesc)
    }

    private func setupDisplayLink() {
        displayLink = CADisplayLink(target: self, selector: #selector(renderFrame))
        if #available(iOS 15.0, *) {
            displayLink?.preferredFrameRateRange = CAFrameRateRange(minimum: 60, maximum: 120, preferred: 120)
        }
        displayLink?.add(to: .main, forMode: .common)
    }

    public func enqueuePixelBuffer(_ buffer: CVPixelBuffer) {
        renderLock.lock()
        currentPixelBuffer = buffer
        renderLock.unlock()
    }

    @objc private func renderFrame() {
        renderLock.lock()
        guard let pixelBuffer = currentPixelBuffer else {
            renderLock.unlock()
            return
        }
        currentPixelBuffer = nil
        renderLock.unlock()

        guard let cache = textureCache,
              let drawable = metalLayer.nextDrawable(),
              let pipeline = pipelineState else { return }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        // Texture plane 0: Y channel
        var yTextureRef: CVMetalTexture?
        CVMetalTextureCacheCreateTextureFromImage(
            kCFAllocatorDefault, cache, pixelBuffer, nil,
            .r8Unorm, width, height, 0, &yTextureRef
        )
        guard let yTexRef = yTextureRef, let yTexture = CVMetalTextureGetTexture(yTexRef) else { return }

        // Texture plane 1: UV channel (CbCr interleaved)
        var uvTextureRef: CVMetalTexture?
        CVMetalTextureCacheCreateTextureFromImage(
            kCFAllocatorDefault, cache, pixelBuffer, nil,
            .rg8Unorm, width / 2, height / 2, 1, &uvTextureRef
        )
        guard let uvTexRef = uvTextureRef, let uvTexture = CVMetalTextureGetTexture(uvTexRef) else { return }

        let passDescriptor = MTLRenderPassDescriptor()
        passDescriptor.colorAttachments[0].texture = drawable.texture
        passDescriptor.colorAttachments[0].loadAction = .dontCare
        passDescriptor.colorAttachments[0].storeAction = .store

        guard let commandBuffer = commandQueue.makeCommandBuffer(),
              let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: passDescriptor) else { return }

        encoder.setRenderPipelineState(pipeline)
        encoder.setFragmentTexture(yTexture, index: 0)
        encoder.setFragmentTexture(uvTexture, index: 1)
        encoder.setFragmentSamplerState(textureSampler, index: 0)
        encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
        encoder.endEncoding()

        commandBuffer.present(drawable)
        commandBuffer.commit()

        // Track FPS
        frameCounter += 1
        let now = CACurrentMediaTime()
        if now - lastFpsTimestamp >= 1.0 {
            let fps = Int(Double(frameCounter) / (now - lastFpsTimestamp))
            onFpsUpdate?(fps)
            frameCounter = 0
            lastFpsTimestamp = now
        }
    }

    private let shadersSource = """
    #include <metal_stdlib>
    using namespace metal;

    struct VertexOut {
        float4 position [[position]];
        float2 texCoords;
    };

    vertex VertexOut vertexShader(uint vertexID [[vertex_id]]) {
        const float2 positions[4] = {
            float2(-1.0, -1.0),
            float2( 1.0, -1.0),
            float2(-1.0,  1.0),
            float2( 1.0,  1.0)
        };

        const float2 texCoords[4] = {
            float2(0.0, 1.0),
            float2(1.0, 1.0),
            float2(0.0, 0.0),
            float2(1.0, 0.0)
        };

        VertexOut out;
        out.position = float4(positions[vertexID], 0.0, 1.0);
        out.texCoords = texCoords[vertexID];
        return out;
    }

    fragment float4 fragmentShaderNV12(VertexOut in [[stage_in]],
                                       texture2d<float, access::sample> yTexture [[texture(0)]],
                                       texture2d<float, access::sample> uvTexture [[texture(1)]],
                                       sampler textureSampler [[sampler(0)]]) {
        float y = yTexture.sample(textureSampler, in.texCoords).r;
        float2 uv = uvTexture.sample(textureSampler, in.texCoords).rg - float2(0.5, 0.5);

        // BT.709 video-range matrix (matches Intel QSV NV12 output)
        float yScaled = 1.16438356 * (y - 0.0627451);
        float r = yScaled + 1.79274107 * uv.y;
        float g = yScaled - 0.21324861 * uv.x - 0.53290933 * uv.y;
        float b = yScaled + 2.11240179 * uv.x;

        return float4(clamp(float3(r, g, b), 0.0, 1.0), 1.0);
    }
    """
}
