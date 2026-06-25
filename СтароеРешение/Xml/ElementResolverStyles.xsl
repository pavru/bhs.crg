<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:fn="http://www.w3.org/2005/xpath-functions" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:lf="urn:BimHouse:Functions" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/MergeNodesStyles.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>
	<xsl:variable name="ExtDef" select="document(//*[string(@xsi:type) = 'ct:Тип.ФайлОбщихДанных']/@uri)"/>
	<xsl:variable name="ExtDataExclusion" select="//*[string(@xsi:type) = 'ct:Тип.ФайлОбщихДанных']/ct:Исключения"/>
	
	<xsl:function name="lf:UserFilter" as="xs:boolean">
		<xsl:param name="Root"/>
		<xsl:param name="Node"/>
		<xsl:variable name="return">
			<xsl:choose>
				<xsl:when test="not($Root/*/ct:Исключения)"><xsl:value-of select="true()"/></xsl:when>
				<xsl:otherwise>
					<xsl:variable name="test">
						<xsl:for-each select="$Root/*/ct:Исключения/ct:Исключение">
							<xsl:variable name="xpath" select="string(@xpath)"/>
							<xsl:variable name="found"><xsl:evaluate xpath="$xpath" context-item="$Node"/></xsl:variable>
							<xsl:if test="count($found/*) > 0">1</xsl:if>
						</xsl:for-each>
						<xsl:if test="$Node[@xsi:type = 'ct:Тип.ФайлОбщихДанных']">1</xsl:if>
					</xsl:variable>
					<xsl:choose>
						<xsl:when test="not(contains(string($test),'1'))"><xsl:value-of select="true()"/></xsl:when>
						<xsl:otherwise><xsl:value-of select="false()"/></xsl:otherwise>
					</xsl:choose>
				</xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
		<xsl:value-of select="$return"/>
	</xsl:function>
	
	<xsl:template name="ResolveElement">
		<xsl:param name="OriginElement"/>
		<xsl:variable name="ExtData">
			<xsl:choose>
				<xsl:when test="$OriginElement/@uri">
					<xsl:copy-of select="document($OriginElement/@uri)"/>
				</xsl:when>
				<xsl:otherwise>
					<xsl:copy-of select="$ExtDef"/>
				</xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
		<xsl:variable name="ElementType" select="string($OriginElement/@xsi:type)"/>
		<xsl:variable name="Node">
			<xsl:choose>
				<xsl:when test="$OriginElement/@ref and not(//*[starts-with($ElementType,string(@xsi:type)) and @id=$OriginElement/@ref]) and not($ExtData//*[starts-with($ElementType,string(@xsi:type)) and @id=$OriginElement/@ref])">
					<xsl:element name="{name($OriginElement)}" namespace="{namespace-uri($OriginElement)}">
						<xsl:element name="Ошибка" namespace="urn:BimHouse:CommonDataType">
							<xsl:text>Определения </xsl:text>
							<xsl:value-of select="name($OriginElement)"/>
							<xsl:text> с идентификатором </xsl:text>
							<xsl:value-of select="$OriginElement/@ref"/>
							<xsl:text> не найдено</xsl:text>
						</xsl:element>
					</xsl:element>
				</xsl:when>
				<xsl:when test="$OriginElement/@ref and //*[starts-with($ElementType,string(@xsi:type)) and @id=$OriginElement/@ref]">
					<xsl:copy-of select="//*[starts-with($ElementType,string(@xsi:type)) and @id=$OriginElement/@ref]"/>
				</xsl:when>
				<xsl:when test="$OriginElement/@ref and $ExtData//*[starts-with($ElementType,string(@xsi:type)) and @id=$OriginElement/@ref]">
					<xsl:copy-of select="$ExtData//*[starts-with($ElementType,string(@xsi:type)) and @id=$OriginElement/@ref]"/>
				</xsl:when>
				<xsl:otherwise>
					<xsl:copy-of select="$OriginElement"/>
				</xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
<!--		<xsl:variable name="SubResolved">
			<xsl:apply-templates select="$Node" mode="resolving"/>
		</xsl:variable>-->
		<xsl:variable name="Merged">
			<xsl:call-template name="MergeNodes">
				<xsl:with-param name="OrigNode" select="$OriginElement"/>
				<xsl:with-param name="RefNode" select="$Node/*/*"/>
			</xsl:call-template>
		</xsl:variable>
		<xsl:copy-of select="$Merged/*"/>
	</xsl:template>

	<xsl:template match="@*|node()" mode="resolving">
		<xsl:copy>
			<xsl:apply-templates select="@*|node()[lf:UserFilter(/,.)]" mode="resolving"/>
		</xsl:copy>
	</xsl:template>
	<xsl:template match="@*|node()" mode="presenting">
		<xsl:copy>
			<xsl:apply-templates select="@*|node()" mode="presenting"/>
		</xsl:copy>
	</xsl:template>
	
	<xsl:template match="*[@xsi:type and @ref]" name="ElementWithresolving" mode="resolving">
		<xsl:variable name="OriginNode" select="."/>
		<xsl:variable name="OriginAttrs" select="@*[name() != 'id' and name() != 'ref']"/>
		<xsl:variable name="Node">
			<xsl:call-template name="ResolveElement">
				<xsl:with-param name="OriginElement" select="."/>
			</xsl:call-template>
		</xsl:variable>
		
		<xsl:variable name="NewNode">
			<xsl:for-each select="$Node/*">
				<xsl:copy>
					<xsl:namespace name="" select="namespace-uri()"/>
					<xsl:copy-of select="@*"/>
					<xsl:apply-templates select="@*|node()[lf:UserFilter(/,.)]" mode="resolving"/>
				</xsl:copy>
			</xsl:for-each>
		</xsl:variable>
		<xsl:copy-of select="$NewNode"/>
	</xsl:template>

	<xsl:template match="*[node() and not(processing-instruction()) and not(comment())]">
		<xsl:variable name="OriginResolved">
			<xsl:call-template name="ElementWithresolving"/>
		</xsl:variable>
		<xsl:variable name="OriginWithpresenting">
			<xsl:for-each select="$OriginResolved/node()">
				<xsl:copy>
					<xsl:apply-templates select="node()" mode="presenting"/>
				</xsl:copy>
			</xsl:for-each>
		</xsl:variable>
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:if test="not(parent::*)">
				<xsl:copy-of select="@*[name() != 'id' and name() != 'ref']"/>
				<xsl:variable name="Spaces" select="."/>
				<xsl:for-each select="./namespace::*">
					<xsl:variable name="xmlns" select="string(name())"/>
					<xsl:variable name="uri" select="string($Spaces/namespace::*[name() = $xmlns])"/>
					<xsl:if test="starts-with($uri,'urn:BimHouse') and $xmlns != ''">
						<xsl:namespace name="{$xmlns}" select="$uri"/>
					</xsl:if>
				</xsl:for-each>
			</xsl:if>
			<xsl:for-each select="$OriginWithpresenting/*/*">
				<xsl:copy-of select="."/>
			</xsl:for-each>
		</xsl:element>
	</xsl:template>
	
	<xsl:template match="@*">
		<xsl:copy>
			<xsl:apply-templates select="@*"/>
		</xsl:copy>
	</xsl:template>

</xsl:stylesheet>