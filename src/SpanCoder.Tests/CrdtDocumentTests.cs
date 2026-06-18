using System;
using System.Collections.Generic;
using Xunit;
using SpanCoder.Contracts;

namespace SpanCoder.Tests
{
    public class CrdtDocumentTests
    {
        [Fact]
        public void TestGeneratePositionBetween()
        {
            // Empty document case
            int[] pos1 = CrdtDocument.GeneratePositionBetween(null, null);
            Assert.True(pos1.Length > 0);

            // Inserting between null and first node
            int[] pos2 = CrdtDocument.GeneratePositionBetween(null, pos1);
            Assert.Equal(-1, PositionComparer.Instance.Compare(pos2, pos1));

            // Inserting between first node and null
            int[] pos3 = CrdtDocument.GeneratePositionBetween(pos1, null);
            Assert.Equal(1, PositionComparer.Instance.Compare(pos3, pos1));

            // Inserting between two adjacent simple positions
            int[] prev = new int[] { 10 };
            int[] next = new int[] { 11 };
            int[] posBetween = CrdtDocument.GeneratePositionBetween(prev, next);
            Assert.Equal(1, PositionComparer.Instance.Compare(posBetween, prev));
            Assert.Equal(-1, PositionComparer.Instance.Compare(posBetween, next));
            Assert.Equal(new int[] { 10, 5000 }, posBetween);
        }

        [Fact]
        public void TestLocalInsertDelete()
        {
            var doc = new CrdtDocument("client1");

            // Insert "H", "e", "l", "l", "o"
            doc.LocalInsert(0, 'H');
            doc.LocalInsert(1, 'e');
            doc.LocalInsert(2, 'l');
            doc.LocalInsert(3, 'l');
            doc.LocalInsert(4, 'o');

            Assert.Equal("Hello", doc.GetText());

            // Delete 'e' at offset 1
            var delNode = doc.LocalDelete(1);
            Assert.NotNull(delNode);
            Assert.Equal('e', delNode.Value);
            Assert.True(delNode.IsDeleted);

            Assert.Equal("Hllo", doc.GetText());

            // Insert 'a' at offset 1 (to make "Hallo")
            doc.LocalInsert(1, 'a');
            Assert.Equal("Hallo", doc.GetText());
        }

        [Fact]
        public void TestRemoteConflictResolution()
        {
            // Simulate two users starting from "Hello"
            var docA = new CrdtDocument("clientA");
            var docB = new CrdtDocument("clientB");

            // Initialize both with "Hello" using A's nodes
            var nodeH = docA.LocalInsert(0, 'H');
            var nodeE = docA.LocalInsert(1, 'e');
            var nodeL1 = docA.LocalInsert(2, 'l');
            var nodeL2 = docA.LocalInsert(3, 'l');
            var nodeO = docA.LocalInsert(4, 'o');

            var state = docA.GetState();
            docB.InitializeFromState(state);

            // User A inserts '!' at offset 5 (making "Hello!")
            var insA = docA.LocalInsert(5, '!');
            
            // User B inserts '?' at offset 5 concurrently (making "Hello?")
            var insB = docB.LocalInsert(5, '?');

            // Apply B's edit to A
            bool appliedToA = docA.ApplyRemoteInsert(new CollabInsertMessage
            {
                Position = insB.Position,
                Value = insB.Value,
                ClientId = insB.ClientId,
                Clock = insB.Clock
            });
            Assert.True(appliedToA);

            // Apply A's edit to B
            bool appliedToB = docB.ApplyRemoteInsert(new CollabInsertMessage
            {
                Position = insA.Position,
                Value = insA.Value,
                ClientId = insA.ClientId,
                Clock = insA.Clock
            });
            Assert.True(appliedToB);

            // Both documents must converge to the exact same text!
            string textA = docA.GetText();
            string textB = docB.GetText();

            Assert.Equal(textA, textB);
            
            // Since clientA < clientB lexicographically, if the positions were identical
            // clientA's node would be sorted before clientB's node, or vice-versa.
            // In either case, the string contents must be identical.
            Assert.Contains("!", textA);
            Assert.Contains("?", textA);
        }
    }
}
