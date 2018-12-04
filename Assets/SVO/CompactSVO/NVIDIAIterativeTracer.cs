using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;
using RT.CS;

namespace RT.CS {
public class NVIDIAIterativeNaiveTracer : CompactSVO.CompactSVOTracer {
	private Node ExpandSVO(List<uint> svo) {
		Node root = new Node(new Vector3(-1, -1, -1), 2, 1, false);
		ExpandSVOAux(root, 0, 1, svo);
		return root;
	}

	/*
	    child pointer | valid mask | leaf mask
            16			   8			8
	 */
	private void ExpandSVOAux(Node node, int nodeIndex, int level, List<uint> svo) { 
		ChildDescriptor descriptor = new ChildDescriptor(svo[nodeIndex]); 
 
		node.Children = new Node[8]; 
		int pointer = descriptor.childPointer;
		double half = node.Size/2d;

		for(int childNum = 0; childNum < 8; childNum++) { 
			if(descriptor.Valid(childNum)) {
				bool leaf = descriptor.Leaf(childNum);

				Node child = new Node(node.Position + Constants.vfoffsets[childNum] * (float)(half), half, level + 1, leaf);
				node.Children[childNum] = child;

				if(!leaf) {
					ExpandSVOAux(node.Children[childNum], pointer++, level + 1, svo);
				}
			}
		}
	}

	/*
		Ray Tracing methods
		Returns a list of nodes that intersect a ray (in sorted order)
	 */
	public List<SVONode> Trace(UnityEngine.Ray ray, List<uint> svo) {
		List<Node> intersectedNodes = new List<Node>();
		RayStep(ExpandSVO(svo), ray.origin, ray.direction, intersectedNodes);
		return intersectedNodes.ConvertAll(node => (SVONode)node).ToList();
	}

	void CastRay(
		int root, // In: Octree root (pointer to global mem).
		Vector3 p, // In: Ray origin (shared mem).
		Vector3 d, // In: Ray direction (shared mem).
		float ray_size_coef, // In: LOD at ray origin (shared mem).
		float ray_size_bias, // In: LOD increase along ray (register).
		ref float hit_t, // Out: Hit t-value (register).
		ref Vector3 hit_pos, // Out: Hit position (register).
		ref int hit_parent, // Out: Hit parent voxel (pointer to global mem).
		ref int hit_idx, // Out: Hit child slot index (register).
		ref int hit_scale) // Out: Hit scale (register).
	{
		const int s_max = 23; // Maximum scale (number of float mantissa bits).
		float epsilon = Mathf.Pow(2, -s_max);
		Vector2[] stack = new Vector2[s_max + 1]; // Stack of parent voxels (local mem).

		// Get rid of small ray direction components to avoid division by zero.
		if (Mathf.Abs(d.x) < epsilon) d.x += epsilon;
		if (Mathf.Abs(d.y) < epsilon) d.y += epsilon;
		if (Mathf.Abs(d.z) < epsilon) d.z += epsilon;

		// Precompute the coefficients of tx(x), ty(y), and tz(z).
		// The octree is assumed to reside at coordinates [1, 2].
		float tx_coef = 1.0f / -Mathf.Abs(d.x);
		float ty_coef = 1.0f / -Mathf.Abs(d.y);
		float tz_coef = 1.0f / -Mathf.Abs(d.z);
		float tx_bias = tx_coef * p.x;
		float ty_bias = ty_coef * p.y;
		float tz_bias = tz_coef * p.z;

		// Select octant mask to mirror the coordinate system so
		// that ray direction is negative along each axis.
		int octant_mask = 7;
		if (d.x > 0.0f) {
			octant_mask = octant_mask ^ 1; 
			tx_bias = 3.0f * tx_coef - tx_bias;
		}
		if (d.y > 0.0f) {
			octant_mask = octant_mask ^ 2; 
			ty_bias = 3.0f * ty_coef - ty_bias;
		}
		if (d.z > 0.0f) {
			octant_mask = octant_mask ^ 4; 
			tz_bias = 3.0f * tz_coef - tz_bias;
		}

		// Initialize the active span of t-values.
		float t_min = Mathf.Max(Mathf.Max(2.0f * tx_coef - tx_bias, 2.0f * ty_coef - ty_bias), 2.0f * tz_coef - tz_bias);
		float t_max = Mathf.Min(Mathf.Min(tx_coef - tx_bias, ty_coef - ty_bias), tz_coef - tz_bias);
		float h = t_max;
		t_min = Mathf.Max(t_min, 0.0f);
		t_max = Mathf.Min(t_max, 1.0f);

		// Initialize the current voxel to the first child of the root.
		int parent = root;
		Vector2Int child_descriptor = new Vector2Int(0, 0); // invalid until fetched
		int idx = 0;
		Vector3 pos = new Vector3(1.0f, 1.0f, 1.0f);
		int scale = s_max - 1;
		float scale_exp2 = 0.5f; // exp2f(scale - s_max)

		if (1.5f * tx_coef - tx_bias > t_min) {
			idx = idx ^ 1; 
			pos.x = 1.5f;
		}
		if (1.5f * ty_coef - ty_bias > t_min) {
			idx = idx ^ 2; 
			pos.y = 1.5f;
		}
		if (1.5f * tz_coef - tz_bias > t_min) {
			idx = idx ^ 4; 
			pos.z = 1.5f;
		}

		// Traverse voxels along the ray as long as the current voxel
		// stays within the octree.
		while (scale < s_max)
		{
			// Fetch child descriptor unless it is already valid.
			if (child_descriptor.x == 0)
				child_descriptor = *(int2*)parent;

			// Determine maximum t-value of the cube by evaluating
			// tx(), ty(), and tz() at its corner.
			float tx_corner = pos.x * tx_coef - tx_bias;
			float ty_corner = pos.y * ty_coef - ty_bias;
			float tz_corner = pos.z * tz_coef - tz_bias;
			float tc_max = fminf(fminf(tx_corner, ty_corner), tz_corner);

			// Process voxel if the corresponding bit in valid mask is set
			// and the active t-span is non-empty.
			int child_shift = idx ˆ octant_mask; // permute child slots based on the mirroring
			int child_masks = child_descriptor.x << child_shift;

			if ((child_masks & 0x8000) != 0 && t_min <= t_max)
			{
				// Terminate if the voxel is small enough.
				if (tc_max * ray_size_coef + ray_size_bias >= scale_exp2)
					break; // at t_min

				// INTERSECT
				// Intersect active t-span with the cube and evaluate
				// tx(), ty(), and tz() at the center of the voxel.
				float tv_max = fminf(t_max, tc_max);
				float half = scale_exp2 * 0.5f;
				float tx_center = half * tx_coef + tx_corner;
				float ty_center = half * ty_coef + ty_corner;
				float tz_center = half * tz_coef + tz_corner;

				// Descend to the first child if the resulting t-span is non-empty.
				if (t_min <= tv_max)
				{
					// Terminate if the corresponding bit in the non-leaf mask is not set.
					if ((child_masks & 0x0080) == 0)
						break; // at t_min (overridden with tv_min).
						
					// PUSH
					// Write current parent to the stack.
					if (tc_max < h)
						stack[scale] = make_int2((int)parent, __float_as_int(t_max));
					h = tc_max;

					// Find child descriptor corresponding to the current voxel.
					int ofs = (unsigned int)child_descriptor.x >> 17; // child pointer
					if ((child_descriptor.x & 0x10000) != 0) // far
						ofs = parent[ofs * 2]; // far pointer
					ofs += popc8(child_masks & 0x7F);
					parent += ofs * 2;

					// Select child voxel that the ray enters first.
					idx = 0;
					scale--;
					scale_exp2 = half;
					if (tx_center > t_min) idx ˆ= 1, pos.x += scale_exp2;
					if (ty_center > t_min) idx ˆ= 2, pos.y += scale_exp2;
					if (tz_center > t_min) idx ˆ= 4, pos.z += scale_exp2;

					// Update active t-span and invalidate cached child descriptor.
					t_max = tv_max;
					child_descriptor.x = 0;
					continue;
				}
			}
			
			// ADVANCE
			// Step along the ray.
			int step_mask = 0;
			if (tx_corner <= tc_max) step_mask ˆ= 1, pos.x -= scale_exp2;
			if (ty_corner <= tc_max) step_mask ˆ= 2, pos.y -= scale_exp2;
			if (tz_corner <= tc_max) step_mask ˆ= 4, pos.z -= scale_exp2;

			// Update active t-span and flip bits of the child slot index.
			t_min = tc_max;
			idx ˆ= step_mask;

			// Proceed with pop if the bit flips disagree with the ray direction.
			if ((idx & step_mask) != 0)
			{
				// POP
				// Find the highest differing bit between the two positions.
				unsigned int differing_bits = 0;
				if ((step_mask & 1) != 0) differing_bits |= __float_as_int(pos.x) ˆ __float_as_int(pos.x + scale_exp2);
				if ((step_mask & 2) != 0) differing_bits |= __float_as_int(pos.y) ˆ __float_as_int(pos.y + scale_exp2);
				if ((step_mask & 4) != 0) differing_bits |= __float_as_int(pos.z) ˆ __float_as_int(pos.z + scale_exp2);
				scale = (__float_as_int((float)differing_bits) >> 23) - 127; // position of the highest bit
				scale_exp2 = __int_as_float((scale - s_max + 127) << 23); // exp2f(scale - s_max)

				// Restore parent voxel from the stack.
				int2 stackEntry = stack[scale];
				parent = (int*)stackEntry.x;
				t_max = __int_as_float(stackEntry.y);

				// Round cube position and extract child slot index.
				int shx = __float_as_int(pos.x) >> scale;
				int shy = __float_as_int(pos.y) >> scale;
				int shz = __float_as_int(pos.z) >> scale;
				pos.x = __int_as_float(shx << scale);
				pos.y = __int_as_float(shy << scale);
				pos.z = __int_as_float(shz << scale);
				idx = (shx & 1) | ((shy & 1) << 1) | ((shz & 1) << 2);

				// Prevent same parent from being stored again and invalidate cached child descriptor.
				h = 0.0f;
				child_descriptor.x = 0;
			}
		}

		// Indicate miss if we are outside the octree.
		if (scale >= s_max)
			t_min = 2.0f;

		// Undo mirroring of the coordinate system.
		if ((octant_mask & 1) == 0) pos.x = 3.0f - scale_exp2 - pos.x;
		if ((octant_mask & 2) == 0) pos.y = 3.0f - scale_exp2 - pos.y;
		if ((octant_mask & 4) == 0) pos.z = 3.0f - scale_exp2 - pos.z;

		// Output results.
		hit_t = t_min;
		hit_pos.x = fminf(fmaxf(p.x + t_min * d.x, pos.x + epsilon), pos.x + scale_exp2 - epsilon);
		hit_pos.y = fminf(fmaxf(p.y + t_min * d.y, pos.y + epsilon), pos.y + scale_exp2 - epsilon);
		hit_pos.z = fminf(fmaxf(p.z + t_min * d.z, pos.z + epsilon), pos.z + scale_exp2 - epsilon);
		hit_parent = parent;
		hit_idx = idx ˆ octant_mask ˆ 7;
		hit_scale = scale;
	}

	/*
		Debug Methods
	 */

	public List<SVONode> GetAllNodes(List<uint> svo) {
		List<SVONode> nodes = new List<SVONode>();
		testRoot = ExpandSVO(svo);
		GetAllNodesAux(ExpandSVO(svo), nodes);
		return nodes;
	}

	private void GetAllNodesAux(Node node, List<SVONode> nodes) {
		if(node == null) { return; }
		
		nodes.Add(node);

		if(node.Children != null) {
			for(int i = 0; i < 8; i++) {
				GetAllNodesAux(node.Children[i], nodes);
			}
		}
	}
	
	public void DrawGizmos(float scale) {
	}

	// Test the tracing functionality

	public static Node testRoot;
	static IterativeNaiveTracer() {
		Debug.Log("Attempting testGetTTest");

		Vector3 t0 = new Vector3(0, 0, 0);
		Vector3 tm = new Vector3(0.5f, 0.5f, 0.5f);
		Vector3 t1 = new Vector3(1, 1, 1);

		string result = "GetTTest Results\n\n";
		for(int currNode = 0; currNode < 8; currNode++) {
			Vector3 ct0 = getT0(t0, tm, currNode);
			Vector3 ct1 = getT1(tm, t1, currNode); 


			int[] arr = new int[] {4,2,1, 5,3,8, 6,8,3, 7,8,8, 8,6,5, 8,7,8, 8,8,7, 8,8,8};
			Vector3 t = getT1(tm, t1, currNode);

			result += "currNode " + currNode + ": ct0 " + ct0 + ", ct1 " + ct1 + "\n";
			result += "newNode params: (" + t.x + ", " + arr[3 * currNode] + ", " + t.y + ", " + arr[1 + 3*currNode] + ", " + t.z + ", " + arr[2 + 3*currNode] + ")\n";
		}

		Debug.Log(result); 

	}

}
}