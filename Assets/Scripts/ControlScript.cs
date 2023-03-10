// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
//
// Ritchie Lozada (rlozada@microsoft.com)


//#define VP8_ENCODING

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;


#if !UNITY_EDITOR
using Org.WebRtc;
using WebRtcWrapper;
using PeerConnectionClient.Model;
using PeerConnectionClient.Signalling;
using PeerConnectionClient.Utilities;
#endif

public class ControlScript : MonoBehaviour
{
    private const int textureWidth = 1280;
    private const int textureHeight = 720;
    public Text StatusText;
    public Text MessageText;
    public InputField ServerInputTextField;
    public InputField PeerInputTextField;
    public InputField MessageInputField;
    public Renderer RenderTexture;
    public Transform VirtualCamera;
    public float TextureScale = 1f;
    public int PluginMode = 0;

    private Transform camTransform;
    private Vector3 prevPos;
    private Quaternion prevRot;    

    private int frameCounter = 0;
    private int fpsCounter = 0;
    private float fpsCount = 0f;
    private float startTime = 0;
    private float endTime = 0;

#if !UNITY_EDITOR
    private WebRtcControl _webRtcControl;
    private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();
#else
    private static readonly Queue<Action> _executionQueue = new Queue<Action>();
#endif
    private bool frame_ready_receive = true;
    private string messageText;

#region Graphics Low-Level Plugin DLL Setup
#if !UNITY_EDITOR
    public RawVideoSource rawVideo;
    public EncodedVideoSource encodedVideo;
    private MediaVideoTrack _peerVideoTrack;

    [DllImport("TexturesUWP")]
    private static extern void SetTextureFromUnity(System.IntPtr texture, int w, int h);

    [DllImport("TexturesUWP")]
    private static extern void ProcessRawFrame(uint w, uint h, IntPtr yPlane, uint yStride, IntPtr uPlane, uint uStride,
        IntPtr vPlane, uint vStride);

    [DllImport("TexturesUWP")]
    private static extern void ProcessH264Frame(uint w, uint h, IntPtr data, uint dataSize);

    [DllImport("TexturesUWP")]
    private static extern IntPtr GetRenderEventFunc();

    [DllImport("TexturesUWP")]
    private static extern void SetPluginMode(int mode);
#endif
    #endregion

    void Awake()
    {       
        // Local Dev Setup - Define Host Workstation IP Address
        ServerInputTextField.text = "192.168.0.5:8888";
    }

    void Start()
    {
        camTransform = Camera.main.transform;
        prevPos = camTransform.position;
        prevRot = camTransform.rotation;

#if !UNITY_EDITOR        
        _webRtcControl = new WebRtcControl();
        _webRtcControl.OnInitialized += WebRtcControlOnInitialized;
        _webRtcControl.OnPeerMessageDataReceived += WebRtcControlOnPeerMessageDataReceived;
        _webRtcControl.OnStatusMessageUpdate += WebRtcControlOnStatusMessageUpdate;

        Conductor.Instance.OnAddRemoteStream += Conductor_OnAddRemoteStream;
        _webRtcControl.Initialize();


        // Setup Low-Level Graphics Plugin        
        CreateTextureAndPassToPlugin();
        SetPluginMode(PluginMode);
        StartCoroutine(CallPluginAtEndOfFrames());
#endif
    }

#if !UNITY_EDITOR
    private void Conductor_OnAddRemoteStream(MediaStreamEvent evt)
    {        
        System.Diagnostics.Debug.WriteLine("Conductor_OnAddRemoteStream()");
        _peerVideoTrack = evt.Stream.GetVideoTracks().FirstOrDefault();
        if (_peerVideoTrack != null)
        {
            System.Diagnostics.Debug.WriteLine(
                "Conductor_OnAddRemoteStream() - GetVideoTracks: {0}",
                evt.Stream.GetVideoTracks().Count);
            // Raw Video from VP8 Encoded Sender
            // H264 Encoded Stream does not trigger this event

            // TODO:  Switch between VP8 Decoded RAW or H264 ENCODED Frame
#if VP8_ENCODING
            rawVideo = Media.CreateMedia().CreateRawVideoSource(_peerVideoTrack);
            rawVideo.OnRawVideoFrame += Source_OnRawVideoFrame;
#else
            encodedVideo = Media.CreateMedia().CreateEncodedVideoSource(_peerVideoTrack);
            encodedVideo.OnEncodedVideoFrame += EncodedVideo_OnEncodedVideoFrame;
#endif
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("Conductor_OnAddRemoteStream() - peerVideoTrack NULL");
        }
        _webRtcControl.IsReadyToDisconnect = true;
    }

    private void EncodedVideo_OnEncodedVideoFrame(uint w, uint h, byte[] data)
    {
        frameCounter++;
        fpsCounter++;

        messageText = data.Length.ToString();

        if (data.Length == 0)
            return;

        if (frame_ready_receive)
            frame_ready_receive = false;
        else
            return;

        GCHandle buf = GCHandle.Alloc(data, GCHandleType.Pinned);
        ProcessH264Frame(w, h, buf.AddrOfPinnedObject(), (uint)data.Length);
        buf.Free();
    }

    private void Source_OnRawVideoFrame(uint w, uint h, byte[] yPlane, uint yStride, byte[] vPlane, uint vStride, byte[] uPlane, uint uStride)
    {
        frameCounter++;
        fpsCounter++;

        messageText = string.Format("{0}-{1}\n{2}-{3}\n{4}-{5}", 
            yPlane != null ? yPlane.Length.ToString() : "X", yStride,
            vPlane != null ? vPlane.Length.ToString() : "X", vStride,
            uPlane != null ? uPlane.Length.ToString() : "X", uStride);

        if ((yPlane == null) || (uPlane == null) || (vPlane == null))
            return;

        if (frame_ready_receive)
            frame_ready_receive = false;
        else
            return;

        GCHandle yP = GCHandle.Alloc(yPlane, GCHandleType.Pinned);
        GCHandle uP = GCHandle.Alloc(uPlane, GCHandleType.Pinned);
        GCHandle vP = GCHandle.Alloc(vPlane, GCHandleType.Pinned);
        ProcessRawFrame(w, h, yP.AddrOfPinnedObject(), yStride, uP.AddrOfPinnedObject(), uStride,
            vP.AddrOfPinnedObject(), vStride);
        yP.Free();
        uP.Free();
        vP.Free();        
    }
#endif

    private void WebRtcControlOnInitialized()
    {
        EnqueueAction(OnInitialized);
    }


    private void OnInitialized()
    {
#if !UNITY_EDITOR
        // _webRtcUtils.SelectedVideoCodec = _webRtcUtils.VideoCodecs.FirstOrDefault(x => x.Name.Contains("H264"));
        // _webRtcUtils.IsMicrophoneEnabled = false;
//      //  PeerConnectionClient.Signalling.Conductor.Instance.MuteMicrophone();
#if VP8_ENCODING
        _webRtcUtils.SelectedVideoCodec = _webRtcUtils.VideoCodecs.FirstOrDefault(x => x.Name.Contains("VP8"));
#else
        _webRtcControl.SelectedVideoCodec = _webRtcControl.VideoCodecs.FirstOrDefault(x => x.Name.Contains("H264"));
#endif
#endif
        StatusText.text += "WebRTC Initialized\n";
    }

    private void WebRtcControlOnPeerMessageDataReceived(int peerId, string message)
    {        
        EnqueueAction(() => UpdateMessageText(string.Format("{0}-{1}", peerId, message)));
    }


    private void WebRtcControlOnStatusMessageUpdate(string msg)
    {        
        EnqueueAction(() => UpdateStatusText(string.Format("{0}\n", msg)));
    }

    private void UpdateMessageText(string msg)
    {
        MessageText.text += msg;
    }

    private void UpdateStatusText(string msg)
    {
        StatusText.text += msg;
    }

    public void ConnectToServer()
    {
        var signalhost = ServerInputTextField.text.Split(':');
        var host = string.Empty;
        var port = string.Empty;
        if (signalhost.Length > 1)
        {
            host = signalhost[0];
            port = signalhost[1];
        }
        else
        {
            host = signalhost[0];
            port = "8888";
        }
#if !UNITY_EDITOR
        _webRtcControl.ConnectToServer(host, port, PeerInputTextField.text);
#endif
    }

    public void DisconnectFromServer()
    {
#if !UNITY_EDITOR
        _webRtcControl.DisconnectFromServer();
#endif
    }

    public void ConnectToPeer()
    {
        // TODO: Support Peer Selection        
#if !UNITY_EDITOR
        if(_webRtcControl.Peers.Count > 0)
        {
            _webRtcControl.SelectedPeer = _webRtcControl.Peers[0];
            _webRtcControl.ConnectToPeer();
            endTime = (startTime = Time.time) + 10f;
        }
#endif
    }

    public void DisconnectFromPeer()
    {
#if !UNITY_EDITOR
        if(encodedVideo != null)
        {
            encodedVideo.OnEncodedVideoFrame -= EncodedVideo_OnEncodedVideoFrame;            
        }

        _webRtcControl.DisconnectFromPeer();
#endif
    }

    public void SendMessageToPeer()
    {
#if !UNITY_EDITOR
        _webRtcControl.SendPeerMessageData(MessageInputField.text);
#endif
    }

    public void ClearStatusText()
    {
        StatusText.text = string.Empty;
    }

    public void ClearMessageText()
    {
        MessageText.text = string.Empty;        
    }

    public void EnqueueAction(Action action)
    {
        lock (_executionQueue)
        {
            _executionQueue.Enqueue(action);
        }
    }
    
    void Update()
    {
#region Main Camera Control
//        if (Vector3.Distance(prevPos, camTransform.position) > 0.05f ||
//    Quaternion.Angle(prevRot, camTransform.rotation) > 2f)
//        {
//            prevPos = camTransform.position;
//            prevRot = camTransform.rotation;
//            var eulerRot = prevRot.eulerAngles;
//            var camMsg = string.Format(
//                @"{{""camera-transform"":""{0},{1},{2},{3},{4},{5}""}}",
//                prevPos.x,
//                prevPos.y,
//                prevPos.z,
//                eulerRot.x,
//                eulerRot.y,
//                eulerRot.z);
//
//            _webRtcUtils.SendPeerMessageDataExecute(camMsg);
//        }
#endregion


#region Virtual Camera Control
        if (Vector3.Distance(prevPos, VirtualCamera.position) > 0.05f ||
            Quaternion.Angle(prevRot, VirtualCamera.rotation) > 2f)
        {
            prevPos = VirtualCamera.position;
            prevRot = VirtualCamera.rotation;
            var eulerRot = prevRot.eulerAngles;
            var camMsg = string.Format(
                @"{{""camera-transform"":""{0},{1},{2},{3},{4},{5}""}}",
                prevPos.x,
                prevPos.y,
                prevPos.z,
                eulerRot.x,
                eulerRot.y,
                eulerRot.z);
            
#if !UNITY_EDITOR
            _webRtcControl.SendPeerMessageData(camMsg);
#endif
        }
#endregion


        if (Time.time > endTime)
        {
            fpsCount = (float)fpsCounter / (Time.time - startTime);
            fpsCounter = 0;
            endTime = (startTime = Time.time) + 3;
        }

        MessageText.text = string.Format("Raw Frame: {0}\nFPS: {1}\n{2}", frameCounter, fpsCount, messageText);

#if !UNITY_EDITOR
        lock (_executionQueue)            
        {
            while (!_executionQueue.IsEmpty)
            {
                Action qa;
                if (_executionQueue.TryDequeue(out qa))
                {
                    if(qa != null)
                        qa.Invoke();
                }
            }
        }
#endif
    }

    private void CreateTextureAndPassToPlugin()
    {
#if !UNITY_EDITOR
        RenderTexture.transform.localScale = new Vector3(-TextureScale, (float) textureHeight / textureWidth * TextureScale, 1f);

        Texture2D tex = new Texture2D(textureWidth, textureHeight, TextureFormat.ARGB32, false);        
        tex.filterMode = FilterMode.Point;       
        tex.Apply();
        RenderTexture.material.mainTexture = tex;
        SetTextureFromUnity(tex.GetNativeTexturePtr(), tex.width, tex.height);
#endif
    }

    private IEnumerator CallPluginAtEndOfFrames()
    {
        while (true)
        {
            // Wait until all frame rendering is done
            yield return new WaitForEndOfFrame();

            // Issue a plugin event with arbitrary integer identifier.
            // The plugin can distinguish between different
            // things it needs to do based on this ID.
            // For our simple plugin, it does not matter which ID we pass here.

#if !UNITY_EDITOR

            switch (PluginMode)
            {
                case 0:
                    if (!frame_ready_receive)
                    {
                        GL.IssuePluginEvent(GetRenderEventFunc(), 1);
                        frame_ready_receive = true;
                    }
                    break;
                default:
                    GL.IssuePluginEvent(GetRenderEventFunc(), 1);
                    break;                
            }          
#endif
        }
    }
}
