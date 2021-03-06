﻿Shader "Custom/NoiseViewerShader" {
	Properties {
		_Color ("Color", Color) = (1,1,1,1)
		_MainTex ("Main Tex Albedo (RGB)", 2D) = "white" {}
		_Glossiness ("Smoothness", Range(0,1)) = 0.5
		_Metallic ("Metallic", Range(0,1)) = 0.0
	}
	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 200

		CGPROGRAM
		// Physically based Standard lighting model, and enable shadows on all light types
		#pragma surface surf Standard fullforwardshadows

		// Use shader model 4.0 target, to get nicer looking lighting
		#pragma target 4.0

		sampler2D _MainTex;

		struct Input {
			float2 uv_MainTex;
			float3 worldPos;
		};

		half _Glossiness;
		half _Metallic;
		fixed4 _Color;

		void surf (Input IN, inout SurfaceOutputStandard o) {
			// Albedo comes from a texture tinted by color
			//fixed4 c = tex3D (_MainTex, float3( (IN.uv_MainTex * 2) + float2(0.5, 0.5), IN.worldPos.z)) * _Color;
			fixed4 c = tex2D (_MainTex, IN.uv_MainTex);
			o.Albedo = float3(c.r, c.g, c.b);
			// Metallic and smoothness come from slider variables
			o.Metallic = _Metallic;
			o.Smoothness = _Glossiness;
			o.Alpha = c.a;
		}
		ENDCG
	}
	FallBack "Diffuse"
}
