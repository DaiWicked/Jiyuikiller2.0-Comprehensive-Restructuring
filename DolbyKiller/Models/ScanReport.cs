using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DolbyKiller.Models
{
    /// <summary>
    /// 扫描报告：威胁项列表 + 汇总统计
    /// </summary>
    public class ScanReport
    {
        public ObservableCollection<ThreatItem> Items { get; } = new ObservableCollection<ThreatItem>();

        /// <summary>威胁总数</summary>
        public int TotalCount => Items.Count;

        /// <summary>未清除的威胁数</summary>
        public int ActiveCount => Items.Count(i => !i.IsCleared);

        /// <summary>是否有威胁</summary>
        public bool HasThreats => Items.Count > 0;

        /// <summary>是否全部清除</summary>
        public bool AllCleared => Items.Count > 0 && Items.All(i => i.IsCleared);

        /// <summary>按类型统计</summary>
        public Dictionary<ThreatType, int> CountByType =>
            Items.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.Count());

        public void Add(ThreatItem item)
        {
            Items.Add(item);
        }

        public void Clear()
        {
            Items.Clear();
        }
    }
}
