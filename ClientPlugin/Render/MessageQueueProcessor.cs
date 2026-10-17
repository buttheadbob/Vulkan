using System;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Render;

// Drains the render messages the game queued through MyRenderProxy, with the same frame
// semantics as the stock renderer (MyRender11.ProcessMessageQueue):
// - frames the renderer fell behind on ("pre-frames") only get their state changes applied,
// - Draw/DebugDraw messages are only processed for the frame actually drawn,
// - StateChangeOnce messages are applied once even if a frame is drawn again,
// - non-persistent messages are disposed (returned to the pool) once their frame is done.
internal sealed class MessageQueueProcessor
{
    private readonly MySharedData sharedData;
    private readonly Action<MyRenderMessageBase> handle;

    public FrameProcessStatusEnum Status { get; private set; }

    // Increments for every batch of messages processed together, like MyRender11.m_messageFrameCounter.
    // Offscreen sprite messages of a newer batch replace the older ones of the same target.
    public int FrameId { get; private set; }

    public MessageQueueProcessor(MySharedData sharedData, Action<MyRenderMessageBase> handle)
    {
        this.sharedData = sharedData;
        this.handle = handle;
    }

    public void Process(bool draw)
    {
        Status = FrameProcessStatusEnum.NoProcess;
        while (true)
        {
            var frame = sharedData.GetRenderFrame(out bool isPreFrame);
            if (frame == null)
            {
                Status = FrameProcessStatusEnum.NoFrame;
                break;
            }

            if (isPreFrame)
            {
                if (!frame.Processed)
                    Status = FrameProcessStatusEnum.Skipped;

                ProcessPreFrame(frame, returnShared: true);
                continue;
            }

            if (frame.Processed)
                Status = FrameProcessStatusEnum.AlreadyProcessed;
            else if (Status == FrameProcessStatusEnum.NoProcess)
                Status = FrameProcessStatusEnum.Success;

            ProcessRenderFrame(frame, draw);
            break;
        }

        // Messages sent from the render thread itself are applied after the frame
        ProcessRenderFrame(sharedData.MessagesForNextFrame, draw);
        ProcessPreFrame(sharedData.MessagesForNextFrame);
    }

    private void ProcessRenderFrame(MyUpdateFrame frame, bool draw)
    {
        FrameId++;
        foreach (var message in frame.RenderInput)
        {
            if (!draw && IsDraw(message))
                continue;

            if (frame.Processed && message.MessageClass == MyRenderMessageType.StateChangeOnce)
                continue;

            handle(message);
        }

        frame.Processed = true;
    }

    private void ProcessPreFrame(MyUpdateFrame frame, bool returnShared = false)
    {
        FrameId++;
        if (!frame.Processed)
        {
            foreach (var message in frame.RenderInput)
            {
                if (!IsDraw(message))
                    handle(message);
            }
        }

        lock (frame)
        {
            frame.Cleared = true;
            foreach (var message in frame.RenderInput)
            {
                if (!message.IsPersistent)
                    message.Dispose();
            }

            frame.RenderInput.Clear();
            if (returnShared)
                sharedData.ReturnPreFrame(frame);
        }
    }

    private static bool IsDraw(MyRenderMessageBase message) =>
        message.MessageClass == MyRenderMessageType.Draw || message.MessageClass == MyRenderMessageType.DebugDraw;
}
