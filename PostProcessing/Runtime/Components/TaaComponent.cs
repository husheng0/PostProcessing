using System;

namespace UnityEngine.PostProcessing
{
    public sealed class TaaComponent : PostProcessingComponentRenderTexture<AntialiasingModel>
    {
        static class Uniforms
        {
            internal static int _Jitter               = Shader.PropertyToID("_Jitter");
            internal static int _SharpenParameters    = Shader.PropertyToID("_SharpenParameters");
            internal static int _FinalBlendParameters = Shader.PropertyToID("_FinalBlendParameters");
            internal static int _HistoryTex           = Shader.PropertyToID("_HistoryTex");
            internal static int _MainTex              = Shader.PropertyToID("_MainTex");
        }

        const string k_ShaderString = "Hidden/Post FX/Temporal Anti-aliasing";
        const int k_SampleCount = 8;

        readonly RenderBuffer[] m_MRT = new RenderBuffer[2];

        int m_SampleIndex = 0;
        bool m_ResetHistory = true;

        RenderTexture[] m_HistoryTexture = new RenderTexture[2];

        public override bool active
        {
            get
            {
                return model.enabled
                       && model.settings.method == AntialiasingModel.Method.Taa
                       && SystemInfo.supportsMotionVectors
                       && SystemInfo.supportedRenderTargetCount >= 2
                       && !context.interrupted;
            }
        }

        public override DepthTextureMode GetCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        public Vector2 jitterVector { get; private set; }

        public void ResetHistory()
        {
            m_ResetHistory = true;
        }

        public void SetProjectionMatrix(Func<Vector2, Matrix4x4> jitteredFunc)
        {
            var settings = model.settings.taaSettings;

            var jitter = GenerateRandomOffset();
            jitter *= settings.jitterSpread;

            if (VR.VRSettings.isDeviceActive)
            {
                // This saves off the device generated projection matrices as the non-jittered set
                context.camera.CopyStereoDeviceProjectionMatrixToNonJittered();

                for (Camera.StereoscopicEye eye = Camera.StereoscopicEye.Left; eye <= Camera.StereoscopicEye.Right; eye++)
                {
                    Matrix4x4 originalProj = context.camera.GetStereoNonJitteredProjectionMatrix(eye);

                    var jitteredMatrix = GenerateJitteredMatrixFromOriginal(originalProj, jitter);
                    context.camera.SetStereoProjectionMatrix(eye, jitteredMatrix);
                }
            }
            else
            {
                context.camera.nonJitteredProjectionMatrix = context.camera.projectionMatrix;

                if (jitteredFunc != null)
                {
                    context.camera.projectionMatrix = jitteredFunc(jitter);
                }
                else
                {
                    context.camera.projectionMatrix = context.camera.orthographic
                        ? GetOrthographicProjectionMatrix(jitter)
                        : GetPerspectiveProjectionMatrix(jitter);
                }
            }

#if UNITY_5_5_OR_NEWER
            context.camera.useJitteredProjectionMatrixForTransparentRendering = false;
#endif

            jitter.x /= context.width;
            jitter.y /= context.height;

            var material = context.materialFactory.Get(k_ShaderString);
            material.SetVector(Uniforms._Jitter, jitter);

            jitterVector = jitter;
        }

        public void Render(RenderTexture source, RenderTexture destination)
        {
            var material = context.materialFactory.Get(k_ShaderString);
            material.shaderKeywords = null;

            var settings = model.settings.taaSettings;

            int eyeIndex = (int)context.camera.stereoActiveEye;
            if (eyeIndex == (int)Camera.MonoOrStereoscopicEye.Mono)
                eyeIndex = 0;

            if (m_ResetHistory || m_HistoryTexture[eyeIndex] == null || m_HistoryTexture[eyeIndex].width != source.width || m_HistoryTexture[eyeIndex].height != source.height)
            {
                if (m_HistoryTexture[eyeIndex])
                    RenderTexture.ReleaseTemporary(m_HistoryTexture[eyeIndex]);

                m_HistoryTexture[eyeIndex] = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
                m_HistoryTexture[eyeIndex].name = "TAA History";

                Graphics.Blit(source, m_HistoryTexture[eyeIndex], material, 2);
            }

            const float kMotionAmplification = 100f * 60f;
            material.SetVector(Uniforms._SharpenParameters, new Vector4(settings.sharpen, 0f, 0f, 0f));
            material.SetVector(Uniforms._FinalBlendParameters, new Vector4(settings.stationaryBlending, settings.motionBlending, kMotionAmplification, 0f));
            material.SetTexture(Uniforms._MainTex, source);
            material.SetTexture(Uniforms._HistoryTex, m_HistoryTexture[eyeIndex]);

            var tempHistory = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            tempHistory.name = "TAA History";

            m_MRT[0] = destination.colorBuffer;
            m_MRT[1] = tempHistory.colorBuffer;

            Graphics.SetRenderTarget(m_MRT, source.depthBuffer);
            GraphicsUtils.Blit(material, context.camera.orthographic ? 1 : 0);

            RenderTexture.ReleaseTemporary(m_HistoryTexture[eyeIndex]);
            m_HistoryTexture[eyeIndex] = tempHistory;

            m_ResetHistory = false;
        }

        float GetHaltonValue(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / (float)radix;

            while (index > 0)
            {
                result += (float)(index % radix) * fraction;

                index /= radix;
                fraction /= (float)radix;
            }

            return result;
        }

        Vector2 GenerateRandomOffset()
        {
            var offset = new Vector2(
                    GetHaltonValue(m_SampleIndex & 1023, 2),
                    GetHaltonValue(m_SampleIndex & 1023, 3));

            if (++m_SampleIndex >= k_SampleCount)
                m_SampleIndex = 0;

            return offset;
        }

        // Adapted heavily from PlayDead's TAA code
        // https://github.com/playdeadgames/temporal/blob/master/Assets/Scripts/Extensions.cs
        Matrix4x4 GetPerspectiveProjectionMatrix(Vector2 offset)
        {
            float vertical = Mathf.Tan(0.5f * Mathf.Deg2Rad * context.camera.fieldOfView);
            float horizontal = vertical * context.camera.aspect;

            offset.x *= horizontal / (0.5f * context.width);
            offset.y *= vertical / (0.5f * context.height);

            float left = offset.x - horizontal;
            float right = offset.x + horizontal;
            float top = offset.y + vertical;
            float bottom = offset.y - vertical;

            var matrix = new Matrix4x4();

            matrix[0, 0] = 2f / (right - left);
            matrix[0, 1] = 0f;
            matrix[0, 2] = (right + left) / (right - left);
            matrix[0, 3] = 0f;

            matrix[1, 0] = 0f;
            matrix[1, 1] = 2f / (top - bottom);
            matrix[1, 2] = (top + bottom) / (top - bottom);
            matrix[1, 3] = 0f;

            matrix[2, 0] = 0f;
            matrix[2, 1] = 0f;
            matrix[2, 2] = -(context.camera.farClipPlane + context.camera.nearClipPlane) / (context.camera.farClipPlane - context.camera.nearClipPlane);
            matrix[2, 3] = -(2f * context.camera.farClipPlane * context.camera.nearClipPlane) / (context.camera.farClipPlane - context.camera.nearClipPlane);

            matrix[3, 0] = 0f;
            matrix[3, 1] = 0f;
            matrix[3, 2] = -1f;
            matrix[3, 3] = 0f;

            return matrix;
        }

        Matrix4x4 GetOrthographicProjectionMatrix(Vector2 offset)
        {
            float vertical = context.camera.orthographicSize;
            float horizontal = vertical * context.camera.aspect;

            offset.x *= horizontal / (0.5f * context.width);
            offset.y *= vertical / (0.5f * context.height);

            float left = offset.x - horizontal;
            float right = offset.x + horizontal;
            float top = offset.y + vertical;
            float bottom = offset.y - vertical;

            return Matrix4x4.Ortho(left, right, bottom, top, context.camera.nearClipPlane, context.camera.farClipPlane);
        }

        // We can represent a projection matrix by using the tangents of the frustum half angles.
        // The 'traditional' representation of the values in a projection matrix for
        // left, right, top and bottom are that they represent values on the near clip plane.
        // If we take the matrix element (0,0) as an example, it would be equal to
        // (2 * clipNearPlane) / (right - left).  We can divide the term by clipNearPlane to get
        // 2 / ((right - left) / clipNearPlane), which gives us
        // 2 / (right/clipNearPlane - left/clipNearPlane).  And we can substitue 
        // tan(rightHalfAngle) = (right/clipNearPlane) and tan(leftHalfAngle) = (left/clipNearPlane).
        // Our new term for (0,0) is 2/(rTan - lTan).
        // 
        // Since we have the calculated value for (0,0), we can use that to solve for (rTan - lTan).
        // (rTan - lTan) = 2 / proj(0,0)
        // We can also get the value for (rTan + lTan), as it is the numerator for term (0,2).
        // We have the denominator, so (rTan + lTan) = (rTan - lTan) * proj(0,2) = 2 * proj(0,2) / proj (0,0)
        // We can add (rTan + lTan) and (rTan - lTan) to get 2 * rTan, which can gives us rTan.
        // rTan = ((2 / proj(0,0)) + (2 * proj(0,2) / proj(0,0))) / 2 =
        //        (1 + proj(0,2)) / proj(0,0) 
        // We can derive lTan via proj(0,0) = 2 / (rTan - lTan), which gives us
        // lTan = rTan - 2/proj(0,0)
        //
        // We can repeat these calculations for the top and bottom tangents as well.
        public Matrix4x4 GenerateJitteredMatrixFromOriginal(Matrix4x4 origProj, Vector2 jitter)
        {
            //var rMinusL = 2.0f / origProj[0, 0];
            //var rPlusL = rMinusL * origProj[0, 2];
            //var cVal = rMinusL + rPlusL;
            //var rTanVal = cVal / 2.0f;
            //var lTanVal = rPlusL - rTanVal;

            //var tMinusB = 2.0f / origProj[1, 1];
            //var tPlusB = tMinusB * origProj[1, 2];
            //var dVal = tMinusB + tPlusB;
            //var tTanVal = dVal / 2.0f;
            //var bTanVal = tPlusB - tTanVal;

            var rTan = (1.0f + origProj[0, 2]) / origProj[0, 0];
            var lTan = rTan - (2.0f / origProj[0, 0]);

            var tTan = (1.0f + origProj[1, 2]) / origProj[1, 1];
            var bTan = tTan - (2.0f / origProj[1, 1]);

            float tanVertFov = Math.Abs(tTan) + Math.Abs(bTan);
            float tanHorizFov = Math.Abs(lTan) + Math.Abs(rTan);

            jitter.x *= tanHorizFov / context.width;
            jitter.y *= tanVertFov / context.height;

            float left = jitter.x + lTan;
            float right = jitter.x + rTan;
            float top = jitter.y + tTan;
            float bottom = jitter.y + bTan;

            var jitteredMatrix = new Matrix4x4();

            jitteredMatrix[0, 0] = 2f / (right - left);
            jitteredMatrix[0, 1] = 0f;
            jitteredMatrix[0, 2] = (right + left) / (right - left);
            jitteredMatrix[0, 3] = 0f;

            jitteredMatrix[1, 0] = 0f;
            jitteredMatrix[1, 1] = 2f / (top - bottom);
            jitteredMatrix[1, 2] = (top + bottom) / (top - bottom);
            jitteredMatrix[1, 3] = 0f;

            jitteredMatrix[2, 0] = 0f;
            jitteredMatrix[2, 1] = 0f;
            jitteredMatrix[2, 2] = -(context.camera.farClipPlane + context.camera.nearClipPlane) / (context.camera.farClipPlane - context.camera.nearClipPlane);
            jitteredMatrix[2, 3] = -(2f * context.camera.farClipPlane * context.camera.nearClipPlane) / (context.camera.farClipPlane - context.camera.nearClipPlane);

            jitteredMatrix[3, 0] = 0f;
            jitteredMatrix[3, 1] = 0f;
            jitteredMatrix[3, 2] = -1f;
            jitteredMatrix[3, 3] = 0f;

            return jitteredMatrix;
        }

        public override void OnDisable()
        {
            for (int eye = 0; eye < 2; eye++)
            {
                if (m_HistoryTexture[eye] != null)
                    RenderTexture.ReleaseTemporary(m_HistoryTexture[eye]);

                m_HistoryTexture[eye] = null;
            }
            m_SampleIndex = 0;
            ResetHistory();
        }
    }
}
