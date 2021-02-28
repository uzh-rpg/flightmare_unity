// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Hidden/OFmag"
{
	Properties
	{
		_Sensitivity("Sensitivity", Float) = 1000
	}
	SubShader
	{
		// No culling or depth
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			
			#include "UnityCG.cginc"

 
			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			float4 _CameraMotionVectorsTexture_ST;
			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _CameraMotionVectorsTexture);
				return o;
			}
			
			sampler2D _CameraMotionVectorsTexture;



			float _Sensitivity;

			fixed4 frag (v2f i) : SV_Target
			{
				float2 motion = tex2D(_CameraMotionVectorsTexture, i.uv).rg;
				float value = length(motion)*_Sensitivity;
				// value= _Sensitivity *sqrt((motion.y*motion.y)+(motion.x*motion.x));
				return float4(value, value,value,value);
			}
			ENDCG
		}
	}
}
