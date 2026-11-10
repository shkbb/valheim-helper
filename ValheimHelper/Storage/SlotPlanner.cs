using UnityEngine;

namespace ValheimHelper.Storage
{
    // Puts items into a chest the way people lay out a storage by hand: an item first tops up its own partial
    // stacks, new stacks continue the row of that item (then the row below it), and a kind the chest does not
    // hold yet starts at the beginning of the first free row. Nothing already in the chest is moved.
    internal static class SlotPlanner
    {
        // Returns how many pieces were moved from 'from' into 'to'.
        internal static int Deposit(Inventory from, Inventory to, ItemDrop.ItemData item, int amount)
        {
            int moved = 0;
            if (item.m_shared.m_maxStackSize > 1)
            {
                foreach (ItemDrop.ItemData stack in to.GetAllItemsInGridOrder())
                {
                    if (moved >= amount || item.m_stack <= 0)
                    {
                        break;
                    }
                    if (!stack.IsSameType(item) || stack.m_quality != item.m_quality || stack.m_stack >= stack.m_shared.m_maxStackSize)
                    {
                        continue;
                    }
                    int part = Mathf.Min(amount - moved, stack.m_shared.m_maxStackSize - stack.m_stack);
                    moved += Move(from, to, item, part, stack.m_gridPos.x, stack.m_gridPos.y);
                }
            }
            while (moved < amount && item.m_stack > 0)
            {
                if (!ChooseSlot(to, item, out int x, out int y))
                {
                    break;
                }
                int part = Mathf.Min(amount - moved, item.m_shared.m_maxStackSize);
                int done = Move(from, to, item, part, x, y);
                if (done <= 0)
                {
                    break;
                }
                moved += done;
            }
            return moved;
        }

        private static int Move(Inventory from, Inventory to, ItemDrop.ItemData item, int amount, int x, int y)
        {
            int before = item.m_stack;
            to.MoveItemToThis(from, item, Mathf.Min(amount, before), x, y);
            return before - item.m_stack;
        }

        private static bool ChooseSlot(Inventory inventory, ItemDrop.ItemData item, out int bestX, out int bestY)
        {
            int width = inventory.GetWidth();
            int height = inventory.GetHeight();
            string[,] grid = new string[width, height];
            bool hasSame = false;
            foreach (ItemDrop.ItemData other in inventory.GetAllItems())
            {
                int ox = other.m_gridPos.x;
                int oy = other.m_gridPos.y;
                if (ox >= 0 && oy >= 0 && ox < width && oy < height)
                {
                    grid[ox, oy] = other.m_shared.m_name;
                    hasSame |= other.m_shared.m_name == item.m_shared.m_name;
                }
            }
            string name = item.m_shared.m_name;
            float best = float.MinValue;
            bestX = -1;
            bestY = -1;
            for (int y = 0; y < height; y++)
            {
                int leadingEmpty = 0;
                while (leadingEmpty < width && grid[leadingEmpty, y] == null)
                {
                    leadingEmpty++;
                }
                for (int x = 0; x < width; x++)
                {
                    if (grid[x, y] != null)
                    {
                        continue;
                    }
                    float score = hasSame ? ScoreNextToSame(grid, name, x, y, width, height) : ScoreNewKind(x, y, width, leadingEmpty);
                    if (score > best)
                    {
                        best = score;
                        bestX = x;
                        bestY = y;
                    }
                }
            }
            return bestX >= 0;
        }

        private static float ScoreNextToSame(string[,] grid, string name, int x, int y, int width, int height)
        {
            if (x > 0 && grid[x - 1, y] == name)
            {
                return 1000f - y;
            }
            if (x < width - 1 && grid[x + 1, y] == name)
            {
                return 950f - y;
            }
            if (y > 0 && grid[x, y - 1] == name)
            {
                return 700f - x - y * 0.01f;
            }
            if (y < height - 1 && grid[x, y + 1] == name)
            {
                return 650f - x - y * 0.01f;
            }
            int nearest = int.MaxValue;
            int nearestInRow = int.MaxValue;
            for (int yy = 0; yy < height; yy++)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    if (grid[xx, yy] != name)
                    {
                        continue;
                    }
                    int distance = Mathf.Abs(xx - x) + Mathf.Abs(yy - y);
                    nearest = Mathf.Min(nearest, distance);
                    if (yy == y)
                    {
                        nearestInRow = Mathf.Min(nearestInRow, distance);
                    }
                }
            }
            return nearestInRow != int.MaxValue ? 500f - nearestInRow : 100f - nearest;
        }

        private static float ScoreNewKind(int x, int y, int width, int leadingEmpty)
        {
            // Start of a row whose left part is free (single odd items may sit at its right edge).
            if (x == 0 && leadingEmpty >= Mathf.Min(2, width))
            {
                return 800f + leadingEmpty * 0.1f - y * 5f;
            }
            if (x == width - 1)
            {
                return 400f - y;
            }
            return 100f - (y * width + x) * 0.01f;
        }
    }
}
