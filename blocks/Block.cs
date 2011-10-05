/*
    Copyright (C) 2011 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using Mono.Cecil.Cil;

namespace de4dot.blocks {
	public class Block : BaseBlock {
		List<Instr> instructions = new List<Instr>();

		// List of all explicit (non-fall-through) targets. It's just one if it's a normal
		// branch, but if it's a switch, it could be many targets.
		List<Block> targets;

		// This is the fall through Block (non branch instructions)
		Block fallThrough;

		// All blocks that fall through or branches to this block
		List<Block> sources = new List<Block>();

		public Block FallThrough {
			get { return fallThrough; }
			set { fallThrough = value; }
		}

		public List<Block> Targets {
			get { return targets; }
			set { targets = value; }
		}

		public List<Block> Sources {
			get { return sources; }
		}

		public Instr FirstInstr {
			get {
				if (instructions.Count == 0)
					add(new Instr(Instruction.Create(OpCodes.Nop)));
				return instructions[0];
			}
		}

		public Instr LastInstr {
			get {
				if (instructions.Count == 0)
					add(new Instr(Instruction.Create(OpCodes.Nop)));
				return instructions[instructions.Count - 1];
			}
		}

		public void add(Instr instr) {
			instructions.Add(instr);
		}

		public void insert(int index, Instruction instr) {
			instructions.Insert(index, new Instr(instr));
		}

		public List<Instr> Instructions {
			get { return instructions; }
		}

		// If last instr is a br/br.s, removes it and replaces it with a fall through
		public void removeLastBr() {
			if (!LastInstr.isBr())
				return;

			if (fallThrough != null || (LastInstr.Operand != null && (targets == null || targets.Count != 1)))
				throw new ApplicationException("Invalid block state when last instr is a br/br.s");
			fallThrough = LastInstr.Operand != null ? targets[0] : null;
			targets = null;
			instructions.RemoveAt(instructions.Count - 1);
		}

		public void replace(int index, int num, Instruction instruction) {
			if (num <= 0)
				throw new ArgumentOutOfRangeException("num");
			remove(index, num);
			instructions.Insert(index, new Instr(instruction));
		}

		public void remove(int index, int num) {
			if (index + num > instructions.Count)
				throw new ApplicationException("Overflow");
			if (num > 0 && index + num == instructions.Count && LastInstr.isConditionalBranch())
				disconnectFromFallThroughAndTargets();
			instructions.RemoveRange(index, num);
		}

		public void remove(IEnumerable<int> indexes) {
			var instrsToDelete = new List<int>(indexes);
			instrsToDelete.Sort();
			instrsToDelete.Reverse();
			foreach (var index in instrsToDelete)
				remove(index, 1);
		}

		// Removes all instructions that do nothing, nop and eg. ldc/pop, etc.
		public bool removeNops() {
			bool removed = false;

			bool keepLooping = true;
			while (keepLooping) {
				var instrsToRemove = new List<int>();
				for (int i = 0; i < Instructions.Count; i++) {
					var instr = Instructions[i];
					if (instr.OpCode.Code == Code.Nop) {
						// The nop instruction is auto created when we access LastInstr so
						// make we don't get an infinite loop.
						if (Instructions.Count != 1)
							instrsToRemove.Add(i);
						continue;
					}
					if (i + 1 >= Instructions.Count)
						continue;
					var next = Instructions[i + 1];
					if (instr.isSimpleLoad() && next.isPop()) {
						instrsToRemove.Add(i);
						instrsToRemove.Add(i + 1);
						i++;
						continue;
					}
				}
				keepLooping = instrsToRemove.Count != 0;
				if (keepLooping) {
					removed = true;
					remove(instrsToRemove);
				}
			}

			return removed;
		}

		// Replace the last instructions with a branch to target
		public void replaceLastInstrsWithBranch(int numInstrs, Block target) {
			if (numInstrs < 0 || numInstrs > instructions.Count)
				throw new ApplicationException("Invalid numInstrs to replace with branch");
			if (target == null)
				throw new ApplicationException("Invalid new target, it's null");

			disconnectFromFallThroughAndTargets();
			if (numInstrs > 0)
				instructions.RemoveRange(instructions.Count - numInstrs, numInstrs);
			fallThrough = target;
			target.sources.Add(this);
		}

		public void replaceLastNonBranchWithBranch(int numInstrs, Block target) {
			if (LastInstr.isBr())
				numInstrs++;
			replaceLastInstrsWithBranch(numInstrs, target);
		}

		public void removeDeadBlock() {
			if (sources.Count != 0)
				throw new ApplicationException("Trying to remove a non-dead block");
			removeGuaranteedDeadBlock();
		}

		// Removes a block that has been guaranteed to be dead. This method won't verify
		// that it really is dead.
		public void removeGuaranteedDeadBlock() {
			disconnectFromFallThroughAndTargets();
			Parent = null;
		}

		void disconnectFromFallThroughAndTargets() {
			disconnectFromFallThrough();
			disconnectFromTargets();
		}

		void disconnectFromFallThrough() {
			if (fallThrough != null) {
				disconnectFromBlock(fallThrough);
				fallThrough = null;
			}
		}

		void disconnectFromTargets() {
			if (targets != null) {
				foreach (var target in targets)
					disconnectFromBlock(target);
				targets = null;
			}
		}

		void disconnectFromBlock(Block target) {
			if (!target.sources.Remove(this))
				throw new ApplicationException("Could not remove the block from its target block");
		}

		public int countTargets() {
			int count = fallThrough != null ? 1 : 0;
			if (targets != null)
				count += targets.Count;
			return count;
		}

		// Returns the target iff it has only ONE target. Else it returns null.
		public Block getOnlyTarget() {
			if (countTargets() != 1)
				return null;
			if (fallThrough != null)
				return fallThrough;
			return targets[0];
		}

		// Returns all targets. FallThrough (if not null) is always returned first!
		public IEnumerable<Block> getTargets() {
			if (fallThrough != null)
				yield return fallThrough;
			if (targets != null) {
				foreach (var block in targets)
					yield return block;
			}
		}

		// Returns true iff other is the only block in Sources
		public bool isOnlySource(Block other) {
			return sources.Count == 1 && sources[0] == other;
		}

		// Returns true if we can merge other with this
		public bool canMerge(Block other) {
			if (other == null || other == this && getOnlyTarget() != other || !other.isOnlySource(this))
				return false;
			// If it's eg. a leave, then don't merge them since it clears the stack.
			return LastInstr.isBr() || Instr.isFallThrough(LastInstr.OpCode);
		}

		// Merge two blocks into one
		public void merge(Block other) {
			if (!canMerge(other))
				throw new ApplicationException("Can't merge the two blocks!");

			removeLastBr();		// Get rid of last br/br.s if present

			var newInstructions = new List<Instr>();
			addInstructions(newInstructions, instructions);
			addInstructions(newInstructions, other.instructions);
			instructions = newInstructions;

			disconnectFromFallThroughAndTargets();
			if (other.targets != null)
				targets = new List<Block>(other.targets);
			else
				targets = null;
			fallThrough = other.fallThrough;

			other.disconnectFromFallThroughAndTargets();
			other.Parent = null;
			updateSources();
		}

		void addInstructions(IList<Instr> dest, IEnumerable<Instr> instrs) {
			foreach (var instr in instrs) {
				if (!instr.isNop())
					dest.Add(instr);
			}
		}

		// Update each target's Sources property. Must only be called if this isn't in the
		// Sources list!
		public void updateSources() {
			if (fallThrough != null)
				fallThrough.sources.Add(this);
			if (targets != null) {
				foreach (var target in targets)
					target.sources.Add(this);
			}
		}

		// Returns true if it falls through
		public bool isFallThrough() {
			return targets == null && fallThrough != null;
		}

		public bool canFlipConditionalBranch() {
			return LastInstr.canFlipConditionalBranch();
		}

		public void flipConditionalBranch() {
			if (fallThrough == null || targets == null || targets.Count != 1)
				throw new ApplicationException("Invalid bcc block state");
			LastInstr.flipConditonalBranch();
			var oldFallThrough = fallThrough;
			fallThrough = targets[0];
			targets[0] = oldFallThrough;
		}

		// Returns true if it's a conditional branch
		public bool isConditionalBranch() {
			return LastInstr.isConditionalBranch();
		}
	}
}