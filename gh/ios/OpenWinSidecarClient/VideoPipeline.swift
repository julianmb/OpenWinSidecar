import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

public final class VideoPipeline {
    private var decompressionSession: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?
    public var onFrameDecoded: ((CVPixelBuffer) -> Void)?

    public init() {}

    deinit {
        invalidate()
    }

    public func invalidate() {
        if let session = decompressionSession {
            VTDecompressionSessionInvalidate(session)
            decompressionSession = nil
        }
        formatDescription = nil
    }

    /// Configures the hardware HEVC decoder using an ISO/IEC 14496-15 hvcC description record
    public func configureWithHvcC(hvcCData: Data) -> Bool {
        guard hvcCData.count >= 23 else { return false }

        let numArrays = Int(hvcCData[22])
        var offset = 23
        var vpsData: Data?
        var spsData: Data?
        var ppsData: Data?

        for _ in 0..<numArrays {
            guard offset + 3 <= hvcCData.count else { break }
            let arrayType = hvcCData[offset] & 0x3F
            let numNalus = Int(hvcCData[offset + 1]) << 8 | Int(hvcCData[offset + 2])
            offset += 3

            for _ in 0..<numNalus {
                guard offset + 2 <= hvcCData.count else { break }
                let naluLen = Int(hvcCData[offset]) << 8 | Int(hvcCData[offset + 1])
                offset += 2
                guard offset + naluLen <= hvcCData.count else { break }
                let naluBytes = hvcCData.subdata(in: offset..<(offset + naluLen))
                offset += naluLen

                if arrayType == 32 { vpsData = naluBytes }
                else if arrayType == 33 { spsData = naluBytes }
                else if arrayType == 34 { ppsData = naluBytes }
            }
        }

        guard let sps = spsData, let pps = ppsData else {
            print("[VideoPipeline] Missing SPS or PPS in hvcC")
            return false
        }

        var paramSets: [Data] = []
        if let vps = vpsData { paramSets.append(vps) }
        paramSets.append(sps)
        paramSets.append(pps)

        var newFormatDesc: CMVideoFormatDescription?

        let status: OSStatus
        if paramSets.count == 3 {
            status = paramSets[0].withUnsafeBytes { ptr0 in
                paramSets[1].withUnsafeBytes { ptr1 in
                    paramSets[2].withUnsafeBytes { ptr2 in
                        var pointers = [
                            ptr0.baseAddress!.assumingMemoryBound(to: UInt8.self),
                            ptr1.baseAddress!.assumingMemoryBound(to: UInt8.self),
                            ptr2.baseAddress!.assumingMemoryBound(to: UInt8.self)
                        ]
                        var sizes = [paramSets[0].count, paramSets[1].count, paramSets[2].count]
                        return CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                            allocator: kCFAllocatorDefault,
                            parameterSetCount: 3,
                            parameterSetPointers: &pointers,
                            parameterSetSizes: &sizes,
                            nalUnitHeaderLength: 4,
                            extensions: nil,
                            formatDescriptionOut: &newFormatDesc
                        )
                    }
                }
            }
        } else {
            status = paramSets[0].withUnsafeBytes { ptr0 in
                paramSets[1].withUnsafeBytes { ptr1 in
                    var pointers = [
                        ptr0.baseAddress!.assumingMemoryBound(to: UInt8.self),
                        ptr1.baseAddress!.assumingMemoryBound(to: UInt8.self)
                    ]
                    var sizes = [paramSets[0].count, paramSets[1].count]
                    return CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        allocator: kCFAllocatorDefault,
                        parameterSetCount: 2,
                        parameterSetPointers: &pointers,
                        parameterSetSizes: &sizes,
                        nalUnitHeaderLength: 4,
                        extensions: nil,
                        formatDescriptionOut: &newFormatDesc
                    )
                }
            }
        }

        guard status == noErr, let format = newFormatDesc else {
            print("[VideoPipeline] Failed to create CMVideoFormatDescription: \(status)")
            return false
        }

        self.formatDescription = format
        return createDecompressionSession(format: format)
    }

    private func createDecompressionSession(format: CMVideoFormatDescription) -> Bool {
        invalidate()
        self.formatDescription = format

        let pixelBufferAttributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferMetalCompatibilityKey: true,
            kCVPixelBufferOpenGLCompatibilityKey: false
        ]

        var callbackRecord = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: { (decompressionOutputRefCon, sourceFrameRefCon, status, infoFlags, imageBuffer, presentationTimeStamp, presentationDuration) in
                guard status == noErr, let pixelBuffer = imageBuffer else { return }
                let pipeline = Unmanaged<VideoPipeline>.fromOpaque(decompressionOutputRefCon!).takeUnretainedValue()
                pipeline.onFrameDecoded?(pixelBuffer)
            },
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque()
        )

        var session: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: format,
            videoDecoderSpecification: nil,
            destinationImageBufferAttributes: pixelBufferAttributes as CFDictionary,
            outputCallback: &callbackRecord,
            decompressionSessionOut: &session
        )

        guard status == noErr, let validSession = session else {
            print("[VideoPipeline] VTDecompressionSessionCreate failed: \(status)")
            return false
        }

        VTSessionSetProperty(validSession, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        self.decompressionSession = validSession
        print("[VideoPipeline] Hardware HEVC VTDecompressionSession successfully created")
        return true
    }

    /// Decodes a 4-byte length-prefixed NAL unit packet
    public func decodeChunk(data: Data, timestampUs: Int64) {
        guard let session = decompressionSession, let format = formatDescription else { return }

        let dataLength = data.count
        guard let memoryBlock = malloc(dataLength) else { return }
        data.copyBytes(to: memoryBlock.assumingMemoryBound(to: UInt8.self), count: dataLength)

        var blockBuffer: CMBlockBuffer?
        let blockStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: memoryBlock,
            blockLength: dataLength,
            blockAllocator: kCFAllocatorMalloc,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: dataLength,
            flags: 0,
            blockBufferOut: &blockBuffer
        )

        guard blockStatus == kCMBlockBufferNoErr, let buffer = blockBuffer else {
            free(memoryBlock)
            return
        }

        var sampleBuffer: CMSampleBuffer?
        var timingInfo = CMSampleTimingInfo(
            duration: CMTime.invalid,
            presentationTimeStamp: CMTime(value: timestampUs, timescale: 1_000_000),
            decodeTimeStamp: CMTime.invalid
        )

        let sampleStatus = CMSampleBufferCreate(
            allocator: kCFAllocatorDefault,
            dataBuffer: buffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: format,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timingInfo,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuffer
        )

        guard sampleStatus == noErr, let validSampleBuffer = sampleBuffer else { return }

        var flagsOut: VTDecodeInfoFlags = []
        VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: validSampleBuffer,
            flags: [._EnableAsynchronousDecompression],
            infoFlagsOut: &flagsOut
        ) { [weak self] status, _, imageBuffer, _, _ in
            guard status == noErr, let pixelBuffer = imageBuffer else { return }
            self?.onFrameDecoded?(pixelBuffer)
        }
    }
}
