using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;
using RT.CS;

namespace RT.CS {
public class IterativeNaiveTracer : CompactSVO.CompactSVOTracer {
	private Node ExpandSVO(List<int> svo) {
		Node root = new Node(new Vector3(1, 1, 1), 1, 1, false);
		ExpandSVOAux(root, 0, 1, svo);
		return root;
	}

	/*
	    child pointer | valid mask | leaf mask
            16			   8			8
	 */
	private void ExpandSVOAux(Node node, int nodeIndex, int level, List<int> svo) { 
		ChildDescriptor descriptor = new ChildDescriptor(svo[nodeIndex]); 
 
		node.children = new Node[8]; 
		int pointer = descriptor.childPointer;
		float half = node.size/2;

		for(int childNum = 0; childNum < 8; childNum++) { 
			if(descriptor.Valid(childNum)) {
				bool leaf = descriptor.Leaf(childNum);

				Node child = new Node(node.position + Constants.vfoffsets[childNum] * half, half, level + 1, leaf);
				node.children[childNum] = child;

				if(!leaf) {
					ExpandSVOAux(node.children[childNum], pointer++, level + 1, svo);
				}
			}
		}
	}

	/*
		Ray Tracing methods
		Returns a list of nodes that intersect a ray (in sorted order)
	 */
	public List<SVONode> Trace(UnityEngine.Ray ray, List<int> svo) {
		List<Node> intersectedNodes = new List<Node>();
		RayStep(ExpandSVO(svo), ray.origin, ray.direction, intersectedNodes);
		return intersectedNodes.ConvertAll(node => (SVONode)node).ToList();
	}

	// Find entry node based on tmin and tmax
	// This is done by first finding the maximum component of tmin, which determins the entry plane
	// Then, the max of tmin is compared to tmax to determine the speicifc node index
	private int FirstNode(double tx0, double ty0, double tz0, double txm, double tym, double tzm){
		sbyte answer = 0;

		if(tx0 > ty0) {
			if(tz0 > tx0) { // tz0 max. entry xy
				if(txm < tz0) answer |= 4;
				if(tym < tz0) answer |= 2;
			}
			else { //tx0 max. entry yz
				if(tym < tx0) answer |= 2;
				if(tzm < tx0) answer |= 1;
			}
		} else {
			if(ty0 > tz0) { // ty0 max. entry xz
				if(txm < ty0) answer |= 4;
				if(tzm < ty0) answer |= 1;
			} else { // tz0 max. entry XY
				if(txm < tz0) answer |= 4;
				if(tym < tz0) answer |= 2;
			}
		}
		return (int) answer;
	}

 	private static int NewNode(double txm, int x, double tym, int y, double tzm, int z){
		if(txm < tym){
			if(txm < tzm){return x;}  // YZ plane
		}
		else{
			if(tym < tzm){return y;} // XZ plane
		}
		return z; // XY plane;
	}

	// Goal: 8 bytes
	// first 4 bytes: parent id
	// last 4 bytes: t_max (not vector)
	// current goal: get rid of currNode
	private class ParameterData {
		public Vector3 t1;
		public Node node;
		public int currNode;

		public ParameterData(Vector3 t1, Node node, int currNode) {
			this.t1 = t1;
			this.node = node;
			this.currNode = currNode;
		}
	}

 	private void RayStep(Node root, Vector3 rayOrigin, Vector3 rayDirection, List<Node> intersectedNodes)  {

		// 'a' is used to flip the bits that correspond with a negative ray direction
		// when picking a child node
		sbyte a = 0;

		Vector3 nodeMin = root.position;
		Vector3 nodeMax = root.position + Vector3.one * root.size;

 		if(rayDirection.x < 0) {
			rayOrigin.x = (- (rayOrigin.x - 1.5f)) + 1.5f;
			rayDirection.x = -rayDirection.x;
			a |= 4;
		}
		if(rayDirection.y < 0) { 		
			rayOrigin.y = (- (rayOrigin.y - 1.5f)) + 1.5f;
			rayDirection.y = -rayDirection.y;
			a |= 2;
		}
		if(rayDirection.z < 0) { 		
			rayOrigin.z =  (- (rayOrigin.z - 1.5f)) + 1.5f;
			rayDirection.z = -rayDirection.z;
			a |= 1;
		}

		//if(rayDirection.x == 0) rayDirection.x += 0.00000001f; //float.Epsilon * 500000;
		//if(rayDirection.y == 0) rayDirection.y += float.Epsilon * 128;
		//if(rayDirection.z == 0) rayDirection.z += float.Epsilon * 128;

		//Debug.Log("Root position: " + root.Position);

 		double divx = 1 / rayDirection.x;
		double divy = 1 / rayDirection.y;
		double divz = 1 / rayDirection.z;

		double tx_bias = rayOrigin.x * divx;
		double ty_bias = rayOrigin.y * divy;
		double tz_bias = rayOrigin.z * divz;

 		double tx0 = root.position.x * divx - tx_bias;
		double ty0 = root.position.y * divy - ty_bias;
		double tz0 = root.position.z * divz - tz_bias;
		double tx1 = nodeMax.x * divx - tx_bias;
		double ty1 = nodeMax.y * divy - ty_bias;
		double tz1 = nodeMax.z * divz - tz_bias;

		Vector3 rt0 = new Vector3((float)tx0, (float)ty0, (float)tz0);
		Vector3 rt1 = new Vector3((float)tx1, (float)ty1, (float)tz1);
		Vector3 tdif = rt1 - rt0;

		ParameterData[] stack = new ParameterData[30];
		int sf = 0;
		stack[sf] = new ParameterData(rt1, root, -1);

		Vector3 t0, t1;
		Vector3 pos = new Vector3(1, 1, 1);

		int idx;

 		if(Mathd.Max(tx0,ty0,tz0) >= Mathd.Min(tx1,ty1,tz1)) { 	
			return;
		}

		while(sf >= 0) {
			ParameterData data = stack[sf];
			Node node = data.node;

			if(node != null && pos.x != node.position.x) {
				Debug.Log("Pos.x != node.position.x. Pos.x: " + pos.x + ", node.position.x: " + node.position.x);
			}

			t1 = data.t1;

			if(node == null || data.currNode > 7 || t1.x <= 0 || t1.y <= 0 || t1.z <= 0) {
				// Round position to parent position
				pos = roundPosition(pos, sf);

				sf--;
				continue;
			}


			t0 = new Vector3((float)tx0, (float)ty0, (float)tz0);
			float scale = Mathf.Pow(2, -sf);
			t0 = t1 - scale * tdif; 
			if(Mathd.Max(t0.x,t0.y,t0.z) >= Mathd.Min(t1.x,t1.y,t1.z)) {
				Debug.Log("Mistakenly added a node to be intersected...");
			}

			if(node.leaf){
				intersectedNodes.Add(node);
				pos = roundPosition(pos, sf);
				sf--;
				continue;
			}

			Vector3 tm = 0.5f*(t0 + t1);
			data.currNode = data.currNode == -1 ? FirstNode(t0.x,t0.y,t0.z,tm.x,tm.y,tm.z) : data.currNode;



			if((data.currNode & 1) == 1) {
				pos.x += scale*0.25f;
			}
			if((data.currNode & 2) == 2) {
				pos.y += scale*0.25f;
			}
			if((data.currNode & 4) == 4) {
				pos.z += scale*0.25f;
			}

			Vector3 childT1 = getT1(tm, t1, data.currNode);
			ParameterData nextFrame = new ParameterData(childT1, data.node.children[data.currNode^a], -1);
			data.currNode = getNewNode(tm, t1, data.currNode);				
			stack[++sf] = nextFrame;
		}
	}

	private static Vector3 roundPosition(Vector3 pos, int sf) {
		float[] pos_f = new float[] { pos.x, pos.y, pos.z };
		int[] pos_i = new int[3];


		Buffer.BlockCopy(pos_f, 0, pos_i, 0, 3 * 4);
		int shift = 23 - sf;

		int shx = (pos_i[0] >> shift) << shift;
		int shy = (pos_i[1] >> shift) << shift;
		int shz = (pos_i[2] >> shift) << shift;
		Buffer.BlockCopy(pos_i, 0, pos_f, 0, 3 * 4);


		pos.x = pos_f[0];
		pos.y = pos_f[1];
		pos.z = pos_f[2];
		return pos;
	}

	private static Vector3 getT0(Vector3 t0, Vector3 tm, int currNode) {
		float[] arr = new float[] {t0.x, t0.y, t0.z, tm.x, tm.y, tm.z};
		return new Vector3(arr[((currNode & 4) >> 2) * 3],  arr[1 + ((currNode & 2) >> 1) * 3], arr[2 + (currNode & 1) * 3]);
	}
	private static Vector3 getT1(Vector3 tm, Vector3 t1, int currNode) {
		float[] arr = new float[] {tm.x, tm.y, tm.z, t1.x, t1.y, t1.z};
		return new Vector3(arr[((currNode & 4) >> 2) * 3],  arr[1 + ((currNode & 2) >> 1) * 3], arr[2 + (currNode & 1) * 3]);
	}
	private static int getNewNode(Vector3 tm, Vector3 t1, int currNode) {
		int[] arr = new int[] {4,2,1, 5,3,8, 6,8,3, 7,8,8, 8,6,5, 8,7,8, 8,8,7, 8,8,8};
		Vector3 t = getT1(tm, t1, currNode);
		return NewNode(t.x, arr[3 * currNode], t.y, arr[1 + 3*currNode], t.z, arr[2 + 3*currNode]);
	}

	/*
		Debug Methods
	 */

	public List<SVONode> GetAllNodes(List<int> svo) {
		List<SVONode> nodes = new List<SVONode>();
		testRoot = ExpandSVO(svo);
		GetAllNodesAux(ExpandSVO(svo), nodes);
		return nodes;
	}

	private void GetAllNodesAux(Node node, List<SVONode> nodes) {
		if(node == null) { return; }
		
		nodes.Add(node);

		if(node.children != null) {
			for(int i = 0; i < 8; i++) {
				GetAllNodesAux(node.children[i], nodes);
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