<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:fo="http://www.w3.org/1999/XSL/Format" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:fn="http://www.w3.org/2005/xpath-functions">
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>
	<xsl:template name="MatFromKs2">
		<xsl:param name="Ks2Uri"/>
		<xsl:param name="Ks2Number"/>
		<xsl:variable name="ActNumber" select="string($Ks2Number)"/>
		<xsl:variable name="ActIndex" select="string(//ImplemActs/ImplemAct[@Number = $ActNumber]/@ActIndex)"/>
		<xsl:for-each select="Document/Chapters/Chapter/Position[child::Implementation_V2/Item/@ActIndex = $ActIndex and number(replace(child::Implementation_V2/Item/@Quantity,',','.')) &gt; 0]">
			<xsl:if test="not(child::PriceBase/@OZ) and not(child::PriceBase/@EM) and child::PriceBase/@MT">
				<xsl:element name="ct:Материал" namespace="urn:BimHouse:CommonDataFile">
					<xsl:attribute name="MaterialType"/>
					<xsl:element name="ct:Наименование" namespace="urn:BimHouse:CommonDataType"><xsl:value-of select="@Caption"/></xsl:element>
					<xsl:element name="ct:ЕдиницаИзмерения" namespace="urn:BimHouse:CommonDataType"><xsl:value-of select="@Units"/></xsl:element>
					<xsl:element name="ct:Количество" namespace="urn:BimHouse:CommonDataType"><xsl:value-of select="Implementation_V2/Item/@Quantity"/></xsl:element>
					<xsl:element name="ct:СсылочнаяИнформация" namespace="urn:BimHouse:CommonDataType">КС2:<xsl:value-of select="fn:position()"/></xsl:element>
				</xsl:element>
			</xsl:if>
		</xsl:for-each>
	</xsl:template>
</xsl:stylesheet>