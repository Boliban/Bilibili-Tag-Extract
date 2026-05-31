# Bilibili Tag Extract
  
可以将https://github.com/mundane799699/bilibili-history-wxt  
项目中提取的历史记录Json文件转化为csv文件和合并文件  
智能通过Bilibili API获取视频详细标签
  
# 详细信息
数字为交互模式序号  
  
1 - 统计标签/作者并输出 CSV（使用最新历史文件）  
2 - 获取视频详细标签并输出 JSON（使用最新历史文件）  
3 - 执行 1 + 2  
4 - 自动合并所有新历史文件（增量，不获取标签）  
5 - 自动合并所有新历史文件 + 智能获取详细标签  
  
输入 bilibili-history-wxt 默认名称文件（支持同天多个文件）  
# Config
  
Tag映射"merge_mapping"  
作者出现次数最小统计数"author_min_count"  
忽略指定月份进行统计"exclude_months"  
  