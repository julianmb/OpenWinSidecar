import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

public final class VideoPipeline {
    private var decompressionSession: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?
    public var onFrameDecoded: ((CVPixelBuffer) -> Void)?
    /// Fired on every failed frame with the VideoToolbox status, for host-side diagnosis.
    public var onDecodeError: ((OSStatus) -> Void)?
    /// Fired once the errors look systematic (3 consecutive) and a keyframe is needed.
    public var onSessionRecoveryNeeded: (() -> Void)?
    private var consecutiveDecodeErrors = 0

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
        consecutiveDecodeErrors = 0
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

        // Swift imports the C `decompressionOutputCallback` + `decompressionOutputRefCon`
        // pair as ONE optional closure parameter labelled `outputHandler:`. There is no
        // `outputCallback:` label and no VTDecompressionOutputCallbackRecord - passing one
        // is the "extra argument 'outputCallback' in call" error this file produced.
        // Capturing self weakly also removes the Unmanaged refcon round-trip.
        var session: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: format,
            videoDecoderSpecification: nil,
            destinationImageBufferAttributes: pixelBufferAttributes as CFDictionary,
            outputHandler: { [weak self] _, _, callbackStatus, _, imageBuffer, _, _ in
                guard callbackStatus == noErr, let pixelBuffer = imageBuffer else { return }
                self?.onFrameDecoded?(pixelBuffer)
            },
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

    /// Decodes one access unit of 4-byte length-prefixed NAL units. Keyframes are marked as sync
    /// samples so the decoder can resynchronize cleanly after a fresh IDR.
    public func decodeChunk(data: Data, timestampUs: Int64, isKeyframe: Bool) {
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

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(validSampleBuffer, createIfNecessary: true) as? NSMutableArray,
           let dict = attachments.firstObject as? NSMutableDictionary {
            dict[kCMSampleAttachmentKey_NotSync as String] = isKeyframe ? kCFBooleanFalse : kCFBooleanTrue
        }

        var flagsOut: VTDecodeInfoFlags = []
        // `sessionDecodeFrameOut:` is the Swift name for the C `sessionDecodeFrameOut`
        // out-parameter, and the output handler is a labelled `outputHandler:` - not
        // `infoFlagsOut:` plus a trailing closure.
        VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: validSampleBuffer,
            flags: DecodeFrameFlags([.enableAsynchronousDecompression]),
            imageBufferOut: nil,
            presentationTimeStampOut: nil,
            sessionDecodeFrameOut: &flagsOut,
            frameInfoOut: nil,
            outputHandler: { [weak self] status, _, imageBuffer, _, _ in
            guard let self = self else { return }
            guard status == noErr, let pixelBuffer = imageBuffer else {
                // A corrupt chunk (or stale reference) fails the frame but not the session:
                // after 3 in a row, ask for a fresh keyframe. The server rate-limits encoder
                // restarts, so this cannot flood. Resets on the next good frame.
                self.consecutiveDecodeErrors += 1
                // Report the reason alongside the keyframe request. The server's `decerr:`
                // is its primary signal for diagnosing decoder problems on a given device,
                // and the native client was only ever sending a bare `forceidr`, so every
                // iOS-native failure looked like silence in the host log.
                // `status` is already an OSStatus; pass it straight through rather than
                // re-wrapping it.
                self.onDecodeError?(status)
                if self.consecutiveDecodeErrors >= 3 {
                    self.consecutiveDecodeErrors = 0
                    self.onSessionRecoveryNeeded?()
                }
                return
            }
            self.consecutiveDecodeErrors = 0
            self.onFrameDecoded?(pixelBuffer)
            }
        )
    }
}
